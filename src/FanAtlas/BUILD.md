# FanAtlas – Quellcode

Native WPF-Anwendung für Windows, Zielplattform .NET 8. Keine NuGet-Abhängigkeiten.

## Kompilieren

Benötigt ein .NET SDK mit Windows-Desktop-Referenzpaketen.

    dotnet build FanAtlas.csproj -c Release --configfile NuGet.Config
    dotnet publish FanAtlas.csproj -c Release --no-restore -o release

Die ausführbare Datei und die übrigen Dateien im Ausgabeordner gemeinsam weitergeben.
Für diesen einfachen Entwicklungsbuild müssen die .NET-8-Desktop- und ASP.NET-Core-Laufzeiten auf dem Zielrechner vorhanden sein. Der Installer-Build über `Build.ps1` im Repository-Stamm bringt beide Laufzeiten mit.

## Prüfungen

    FanAtlas.exe --self-test "C:\Pfad\Standard Profil.cueprofile" "C:\Ausgabe\tests.txt"
    FanAtlas.exe --render "C:\Pfad\Standard Profil.cueprofile" "C:\Ausgabe\vorschau"

Die Profiltests beziehen sich ausdrücklich auf das ursprüngliche Profil dieses Auftrags
(13 Zuordnungen / 10 Geräte-IDs / zugewiesene Dragon-Fury-Kurve).
Für andere Profile diese Erwartungswerte anpassen. Alle Testdateien werden unter dem
angegebenen Ausgabeordner erzeugt. Der Render-Test benutzt einen separaten Datenordner.

## Aufbau

- ProfileReader.cs: Sicherer XML-Leser, Zuordnung über Kurven-GUIDs.
- Sensors.cs: Nur lesende NVML-Abfragen und begrenztes Lesen laufender CSV-Dateien.
- Models.cs: Datenmodell, Kurvenprüfung, Interpolation, Benutzereinstellungen.
- Chart.cs: WPF-Kurveneditor und Messwertverlauf.
- MainWindow.xaml / .cs: Deutsche Bedienoberfläche.
- SelfTests.cs: Profil-, CSV-, Persistenz-, Validierungs- und Editorprüfungen.
- StageEditor / StageModels: Lüfterlayout, Zuordnungen und Grenzen.
- LocalBridge / Web: lokale OBS- und Stream-Deck-Verbindung.

Es gibt absichtlich keinen Hardware-Schreibpfad. Eigene Kurven sind lokale Entwürfe,
die der Benutzer in seinem Steuerprogramm übernimmt. Der Dateiexport ist JSON/CSV,
nicht das undokumentierte iCUE-Importformat.

