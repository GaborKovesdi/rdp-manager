# RDP Manager

Telepítés nélkül futó (portable) .NET/WPF alkalmazás mentett RDP-kapcsolatok kezelésére:
importálhatók meglévő `.rdp` fájlok, tárolható hozzájuk felhasználónév és jelszó, majd egy
kattintással (vagy dupla kattintással a listán) csatlakozik és automatikusan bejelentkezik a
távoli asztalra. Minden mentett adat egy mesterjelszóval titkosított tárolóban van.

## Futtatás fejlesztői gépen

```
dotnet run --project src/RdpManager/RdpManager.csproj
```

Tesztek (a címfeldolgozás és a titkosított tároló): `dotnet test`. Kiadáskor a release workflow is
lefuttatja őket, és hibás teszt esetén nem készül exe.

## Letöltés

A kész, telepítés nélkül futó `RdpManager.exe` a GitHub
[Releases](https://github.com/GaborKovesdi/rdp-manager/releases/latest) oldaláról tölthető le.
.NET telepítése nem kell hozzá; az exe-t tedd egy saját mappába, mert az adatait mellé, a `data`
mappába menti.

Új kiadás: egy `v`-vel kezdődő tag (pl. `v1.0.0`) pusholásakor a
`.github/workflows/release.yml` lebuildeli az exe-t, és GitHub Release-ként közzéteszi.

```
git tag v1.0.0
git push origin v1.0.0
```

## Portable, telepítő nélküli build készítése

```
dotnet publish src/RdpManager/RdpManager.csproj -c Release -r win-x64 --self-contained true
```

Az eredmény a `src/RdpManager/bin/Release/net9.0-windows/win-x64/publish/` mappában egyetlen
`RdpManager.exe` fájl, amely bármilyen Windows gépre (.NET telepítése nélkül is) átmásolható és
futtatható. Az adatait (`data/connections.dat`) mindig a saját mappájában tárolja, tehát az exe és
a `data` mappa együtt költöztethető — a mesterjelszóval más gépen és más Windows-fiókkal is
megnyitható.

## Mesterjelszó

Induláskor az alkalmazás mesterjelszót kér:

- **Első indítás**: megadsz egy új mesterjelszót (min. 8 karakter).
- **Későbbi indítások**: a mesterjelszóval oldod fel a tárolót.
- **Csere**: a főablak „Mesterjelszó…” gombjával, a jelenlegi jelszó megadása után. A teljes
  tároló az új jelszóval lesz újratitkosítva; ha a mentés nem sikerül, a régi jelszó marad
  érvényben.

A mesterjelszó sehol nincs eltárolva, csak a titkosítási kulcsot származtatja belőle a program
(PBKDF2-HMAC-SHA256, 600 000 iteráció, véletlen salt). **Ha elfelejted, az adatok nem
állíthatók helyre.**

## Hogyan működik

- **Import**: az "Importálás…" gomb beolvassa a kiválasztott `.rdp` fájl(ok) `full address`,
  `username` és `domain` mezőit, és a többi megjelenítési/session beállítást (felbontás,
  átjárókiszolgáló stb.) is megőrzi. A forrásfájl titkosított jelszóblobja (`password 51`) nem
  kerül be a tárolóba. Mivel exportált `.rdp` fájl soha nem tartalmaz visszafejthető jelszót,
  import után rögtön megnyílik a szerkesztő, ahol megadható a jelszó. A fájlok a gomb helyett
  egyszerűen az ablakba is húzhatók (egyszerre több is).
- **Ütköző nevek**: ha az importált kapcsolat neve már foglalt, a végére sorszám kerül
  („szerver”, „szerver (2)”, „szerver (3)”).
- **Import másik tárolóból**: ugyanezzel a gombbal egy másik RDP Manager `connections.dat`
  fájlja is kiválasztható. A saját mesterjelszavával feloldva a kapcsolatai (jelszavakkal együtt)
  hozzáadódnak a mostaniakhoz; a forrásfájl nem változik. Így egyik példányból a másikba
  másolgatás nélkül átvihetők az adatok. Az a kapcsolat, amelyik névvel, címmel, porttal,
  domainnel és felhasználónévvel már szerepel a listában, nem kerül be még egyszer.
- **Tárolás**: a teljes kapcsolatlista (gépnevek, felhasználónevek, jelszavak együtt) egyetlen
  AES-256-GCM titkosított blobként kerül a `data/connections.dat` fájlba. Mivel a GCM hitelesíti
  is az adatot, a fájl utólagos módosítása (például egy gépnév átírása, hogy a bejelentkezés egy
  támadó szerverére menjen) nem marad észrevétlen: a tároló nem nyílik meg. A mentés ideiglenes
  fájlon keresztül, atomikusan történik, így egy megszakadt mentés nem csonkítja a tárolót.
- **Duplikálás**: a „Duplikálás” gomb (Ctrl+D) a kijelölt kapcsolat másolatát — jelszóval és
  beállításokkal együtt — nyitja meg a szerkesztőben, sorszámozott névvel; mentéskor az eredeti
  alá kerül, Mégse esetén nem jön létre.
- **Speciális beállítások**: a szerkesztő „Speciális beállítások” blokkjában a kapcsolat összes
  többi `.rdp` beállítása (felbontás, több monitor, RD Gateway, átirányítások stb.) soronként,
  `kulcs:típus:érték` formában szerkeszthető. A cím, a felhasználó és a jelszó mindig a fenti
  mezőkből kerül a fájlba; a blokkot kiürítve az alapértelmezett beállítások érvényesek.
- **Sorrend**: a listában a sorokat egérrel át lehet húzni (a vonal mutatja, hová kerül a sor),
  vagy a ↑/↓ gombokkal (Alt+↑/↓) lehet mozgatni; a sorrend a tárolóban is megmarad.
  Oszlopfejlécre kattintva nincs rendezés, hogy a saját sorrend ne keveredjen össze.
- **Kezelés**: indításkor az első kapcsolat ki van jelölve, így az Enter rögtön csatlakozik.
  Egy név elejét begépelve a lista arra a kapcsolatra ugrik. Billentyűk: Enter – csatlakozás,
  F2 – szerkesztés, Ctrl+D – duplikálás, Delete – törlés, Ctrl+C – jelszó másolása, Ctrl+N – új
  kapcsolat, Ctrl+O – importálás. A soron jobb kattintással is elérhetők a műveletek; a gombok csak akkor aktívak, ha
  van kijelölt kapcsolat.
- **Mentett jelszó**: a szerkesztő jelszómezője mindig üresen nyílik, alatta viszont látszik, van-e
  mentett jelszó, és mikor mentették. A régebbi verzióval mentett jelszavaknál nincs időpont.
- **Jelszó másolása**: ahol van mentett jelszó, a sor végén kulcs jelenik meg; arra kattintva vagy
  a kijelölt soron Ctrl+C-vel a jelszó a vágólapra kerül. Egy perc múlva a program leveszi onnan
  (kilépéskor azonnal), hacsak közben már mást nem másoltál. A Windows vágólap-előzményeibe
  (Win+V) és a felhős vágólapra nem kerül be.
- **Megjelenés**: Windows 11-es (Fluent) stílus, amely követi a rendszer világos/sötét módját.
- **Csatlakozás egy kattintással**: a program elindítja az `mstsc.exe`-t egy generált, ideiglenes
  `.rdp` fájllal, amely a mentett megjelenítési beállításokat, a helyes felhasználónevet és a
  jelszót tartalmazza (a Windows saját `password 51` mezőjének formátumában, DPAPI-val
  titkosítva). A fájl egy saját, véletlen nevű mappába kerül (`%TEMP%\rdpmanager\<azonosító>\`),
  és a kapcsolat nevét kapja — a fájlnévben nem használható karakterek helyén `_` áll —, mert az
  `mstsc` ezt a nevet írja ki az RDP-ablak címsorába és a tálcára. Emellett a jelszót a Windows
  Credential Managerbe is beírja a távoli gép — és ha van, az RD Gateway — `TERMSRV/<cím>` neve
  alatt, natív Win32 `CredWrite` hívással; a jelszó soha nem kerül parancssori argumentumba. A
  bejegyzések csak a munkamenetig élnek (`CRED_PERSIST_SESSION`), és az ideiglenes fájllal és
  annak mappájával együtt törlődnek, amikor az RDP-ablak bezárul — illetve az alkalmazásból
  kilépéskor is, ha addig nem történt meg.

## Kompatibilitás

Minden új verzió megnyitja a korábbi verziók által írt `data\connections.dat` tárolót, és a
`data` mappa mindig az exe mellett marad, így frissítéskor elég az exe-t lecserélni. Ezt teszt
rögzíti: a `tests/RdpManager.Tests/Fixtures` mappában a kiadott verziók által írt tárolók vannak
(ezeket soha nem szabad újragenerálni), és a kiadás csak akkor készül el, ha mind megnyílik.

## Ismert korlátok

- A feloldás után a jelszavak a program memóriájában olvasható formában vannak (ez minden
  jelszókezelőnél így van a megnyitott munkamenet alatt).
- A session alatt az ideiglenes `.rdp` fájl a `%TEMP%\rdpmanager\<azonosító>\` mappában van,
  benne a jelszó DPAPI-val titkosítva: ugyanazzal a Windows-fiókkal visszafejthető, ezért a fájl a
  mappájával együtt a session végén (vagy az alkalmazásból kilépéskor) törlődik. Ha a program
  összeomlik vagy erőszakkal leállítják, a mappa ott maradhat; ilyenkor kézzel törölhető.
- Csatlakozáskor a Windows megjeleníti az "ismeretlen közzétevő" figyelmeztetést, mert a generált
  `.rdp` fájl nincs digitálisan aláírva. Ez a Windows RDP-kliens saját védelme.

## Ikon

Az alkalmazásikon a Vonal ikoncsalád „Távoli asztal” tagja; a forrása a `design/app-icon.svg`.
A `src/RdpManager/app.ico` (16–256 px) ebből készül, módosítás után újra kell generálni:

```
python design/build-icon.py
```

(Pillow és numpy kell hozzá.) Az exe, a főablak és a párbeszédablakok mind ezt az egy fájlt
használják.

## Projektszerkezet

- `Models/RdpConnection.cs` – egy mentett kapcsolat adatai.
- `Services/StoreCrypto.cs` – kulcsszármaztatás (PBKDF2) és AES-GCM titkosítás.
- `Services/ConnectionStore.cs` – a titkosított tároló betöltése/atomikus mentése.
- `Services/RdpFileService.cs` – `.rdp` fájlok importálása/generálása.
- `Services/NativeCredentialManager.cs` – Windows Credential Manager P/Invoke wrapper.
- `Services/RdpLauncher.cs` – az `mstsc.exe` indítása és a hitelesítő adat életciklusa.
- `Views/MasterPasswordWindow.xaml(.cs)` – mesterjelszó bekérése induláskor és a mesterjelszó cseréje.
- `Views/ConnectionEditorWindow.xaml(.cs)` – kapcsolat hozzáadása/szerkesztése.
- `Views/RowDragReorder.cs` – a lista sorainak egérrel húzós átrendezése.
- `MainWindow.xaml(.cs)` – a kapcsolatlista és a fő műveletek.
- `tests/RdpManager.Tests` – xUnit tesztek.
