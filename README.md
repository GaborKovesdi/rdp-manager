# RDP Manager

Telepítés nélkül futó (portable) .NET/WPF alkalmazás mentett RDP-kapcsolatok kezelésére:
importálhatók meglévő `.rdp` fájlok, tárolható hozzájuk felhasználónév és jelszó, majd egy
kattintással (vagy dupla kattintással a listán) csatlakozik és automatikusan bejelentkezik a
távoli asztalra. Minden mentett adat egy mesterjelszóval titkosított tárolóban van.

## Futtatás fejlesztői gépen

```
dotnet run --project src/RdpManager/RdpManager.csproj
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

- **Első indítás**: megadsz egy új mesterjelszót (min. 8 karakter). Ha van korábbi, DPAPI-alapú
  `connections.json` tároló a `data` mappában, annak tartalma automatikusan átkerül az új
  titkosított tárolóba, a régi fájl pedig `connections.json.migrated` néven megmarad.
- **Későbbi indítások**: a mesterjelszóval oldod fel a tárolót.

A mesterjelszó sehol nincs eltárolva, csak a titkosítási kulcsot származtatja belőle a program
(PBKDF2-HMAC-SHA256, 600 000 iteráció, véletlen salt). **Ha elfelejted, az adatok nem
állíthatók helyre.**

## Hogyan működik

- **Import**: a "Importálás (.rdp)" gomb beolvassa a kiválasztott `.rdp` fájl(ok) `full address`,
  `username` és `domain` mezőit, és a többi megjelenítési/session beállítást (felbontás,
  átjárókiszolgáló stb.) is megőrzi. A forrásfájl titkosított jelszóblobja (`password 51`) nem
  kerül be a tárolóba. Mivel exportált `.rdp` fájl soha nem tartalmaz visszafejthető jelszót,
  import után rögtön megnyílik a szerkesztő, ahol megadható a jelszó.
- **Tárolás**: a teljes kapcsolatlista (gépnevek, felhasználónevek, jelszavak együtt) egyetlen
  AES-256-GCM titkosított blobként kerül a `data/connections.dat` fájlba. Mivel a GCM hitelesíti
  is az adatot, a fájl utólagos módosítása (például egy gépnév átírása, hogy a bejelentkezés egy
  támadó szerverére menjen) nem marad észrevétlen: a tároló nem nyílik meg. A mentés ideiglenes
  fájlon keresztül, atomikusan történik, így egy megszakadt mentés nem csonkítja a tárolót.
- **Sorrend**: a listában a sorokat egérrel át lehet húzni (a kék vonal mutatja, hová kerül a
  sor), vagy a ↑/↓ gombokkal lehet mozgatni; a sorrend a tárolóban is megmarad. Oszlopfejlécre
  kattintva nincs rendezés, hogy a saját sorrend ne keveredjen össze.
- **Csatlakozás egy kattintással**: a program elindítja az `mstsc.exe`-t egy generált, ideiglenes
  `.rdp` fájllal, amely a mentett megjelenítési beállításokat, a helyes felhasználónevet és a
  jelszót tartalmazza (a Windows saját `password 51` mezőjének formátumában, DPAPI-val
  titkosítva). Emellett a jelszót a Windows Credential Managerbe is beírja a távoli gép — és ha
  van, az RD Gateway — `TERMSRV/<cím>` neve alatt, natív Win32 `CredWrite` hívással; a jelszó soha
  nem kerül parancssori argumentumba. A bejegyzések csak a munkamenetig élnek
  (`CRED_PERSIST_SESSION`), és az ideiglenes fájllal együtt törlődnek, amikor az RDP-ablak
  bezárul — illetve az alkalmazásból kilépéskor is, ha addig nem történt meg.

## Ismert korlátok

- A feloldás után a jelszavak a program memóriájában olvasható formában vannak (ez minden
  jelszókezelőnél így van a megnyitott munkamenet alatt).
- A session alatt az ideiglenes `.rdp` fájl a `%TEMP%` mappában van, benne a jelszó DPAPI-val
  titkosítva: ugyanazzal a Windows-fiókkal visszafejthető, ezért a fájl a session végén (vagy az
  alkalmazásból kilépéskor) törlődik.
- Nincs mesterjelszó-csere funkció; jelenleg csak új tároló létrehozásával lehet jelszót váltani.
- Csatlakozáskor a Windows megjeleníti az "ismeretlen közzétevő" figyelmeztetést, mert a generált
  `.rdp` fájl nincs digitálisan aláírva. Ez a Windows RDP-kliens saját védelme.

## Projektszerkezet

- `Models/RdpConnection.cs` – egy mentett kapcsolat adatai.
- `Services/StoreCrypto.cs` – kulcsszármaztatás (PBKDF2) és AES-GCM titkosítás.
- `Services/ConnectionStore.cs` – a titkosított tároló betöltése/atomikus mentése.
- `Services/LegacyStoreMigration.cs` – a régi, DPAPI-alapú tároló átvétele.
- `Services/RdpFileService.cs` – `.rdp` fájlok importálása/generálása.
- `Services/NativeCredentialManager.cs` – Windows Credential Manager P/Invoke wrapper.
- `Services/RdpLauncher.cs` – az `mstsc.exe` indítása és a hitelesítő adat életciklusa.
- `Views/MasterPasswordWindow.xaml(.cs)` – mesterjelszó bekérése induláskor.
- `Views/ConnectionEditorWindow.xaml(.cs)` – kapcsolat hozzáadása/szerkesztése.
- `Views/RowDragReorder.cs` – a lista sorainak egérrel húzós átrendezése.
- `MainWindow.xaml(.cs)` – a kapcsolatlista és a fő műveletek.
