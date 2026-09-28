# Crazy_Batto FanAtlas

Windows-App für Lüfterkurven, Temperaturen, eine frei anordbare Lüfterbühne, OBS und Stream Deck. Version 0.2.0, Windows 10/11 x64.

![Crazy_Batto](assets/crazy-batto.png)

## Funktionen

- Eigene Kurven erstellen, bearbeiten, speichern und als JSON oder Punktetabelle exportieren.
- Exportierte iCUE-Profile lesen: gespeicherte Lüfterkanäle und tatsächlich zugewiesene Kurven anzeigen.
- NVIDIA-Temperaturen und verfügbare Lüfterwerte über NVML lesen; CSV-Sensorprotokolle ergänzen.
- Jeden erkannten Lüfter mit dem bereitgestellten OIP-Lüfterbild anzeigen. Temperatur in der Mitte, Lüfterwert darunter.
- Lüfterbilder ziehen, Größe, Namen und Sensorzuordnung ändern; Layout speichern und laden.
- Zwei Crazy_Batto-Szenenbilder oder transparenter Hintergrund. OBS-Browserquelle mit 1920 × 1080.
- Stream Deck: einzelne Lüfter auf Tasten anzeigen; Tastendruck wechselt Temperatur/Lüfterwert. Zweite Aktion öffnet einen Kurvenentwurf.
- Deutscher Installer mit Startmenüeintrag, optionaler Desktopverknüpfung und Deinstallation. App und Plugin bringen ihre .NET-Laufzeit mit.

## Was die Anzeige bedeutet

Ein Profilkanal ist noch kein erkannter Live-Lüfter. Historische iCUE-Zuordnungen können doppelt vorkommen. NVML liefert je nach Grafikkarte Lüfterwerte in Prozent; diese werden nicht als gemessene RPM ausgegeben. Ein Lüfter besitzt meist keinen eigenen Temperatursensor: Wähle den gewünschten CPU-, GPU- oder Kühlmittelsensor für die Mitte. Nicht verfügbare oder veraltete Werte erscheinen als „—“.

FanAtlas liest Daten und bearbeitet **Kurvenentwürfe**. Es schreibt keine Hardware-Steuerbefehle und ändert keine iCUE-Profile. Damit bleibt iCUE für die tatsächliche Kühlung zuständig. Für Kühlungswechsel auf Stream Deck verwende die [offizielle Corsair-iCUE-Integration](https://help.corsair.com/hc/en-us/articles/360044964132-iCUE-How-to-Set-up-Elgato-Stream-Deck-iCUE-integration). In einer Multiaktion kannst du deren Kühlungsaktion mit „Kurvenentwurf wählen“ kombinieren; die passenden Profile müssen in iCUE separat eingerichtet sein. Eigene FanAtlas-Kurven lassen sich über ihre Punktetabelle in iCUE nachbauen. Es gibt keinen automatischen iCUE-Import.

## Einrichten

1. `CrazyBatto-FanAtlas-Setup-0.2.0.exe` ausführen und FanAtlas starten.
2. Unter **iCUE-Profil öffnen** dein exportiertes `.cueprofile` auswählen.
3. Für zusätzliche Livewerte in iCUE die Sensorprotokollierung einschalten. Die erzeugte CSV unter **Sensorquellen** auswählen; neue vollständige Logzeilen werden laufend eingelesen.
4. Unter **Lüfterbühne & Stream** die Lüfter anordnen und Temperatursensoren zuordnen. Änderungen werden automatisch gespeichert.
5. **OBS-Adresse kopieren** → in OBS eine Browserquelle anlegen → URL einfügen, Breite 1920, Höhe 1080. FanAtlas muss geöffnet bleiben. Wähle „Transparent für OBS“, wenn die Lüfter über deinem Spiel liegen sollen.
6. `de.crazybatto.fanatlas.streamDeckPlugin` per Doppelklick installieren. In Stream Deck **Crazy_Batto FanAtlas → Lüfter anzeigen** auf eine Taste ziehen und den Lüfter auswählen. Für weitere Lüfter wiederholen.

Die Verbindung bleibt auf `127.0.0.1:17654`. Es wird keine Firewallfreigabe angelegt. Anzeige und Kurvenauswahl verwenden getrennte Schlüssel. Einstellungsdateien und Schlüssel liegen unter `%LOCALAPPDATA%\CrazyBatto\FanAtlas` und bleiben beim Deinstallieren erhalten. Keine Benutzerprofile, Sensorprotokolle oder Schlüssel gehören ins Repository.

## Entwickeln / Bauen

Benötigt: Windows x64, .NET SDK 8 oder neuer, Node.js, Inno Setup 7.1 oder neuer. Das Projekt verwendet WPF und ASP.NET Core ausschließlich für den lokalen OBS-/Plugin-Zugriff.

```powershell
dotnet build src/FanAtlas/FanAtlas.csproj -c Release
dotnet build src/FanAtlas.Deck/FanAtlas.Deck.csproj -c Release
.\Build.ps1 -Iscc 'C:\Pfad\zu\Inno Setup\ISCC.exe'
```

`Build.ps1` erzeugt unter `dist/` die selbstständig lauffähige App, das offizielle Stream-Deck-Paket und den Installer. Die Laufzeitversion kann mit `-RuntimeVersion` angepasst werden. Zum Aktualisieren eine unterstützte .NET-8-Version verwenden.

Entwicklertests mit einem geeigneten eigenen iCUE-Profil:

```powershell
src/FanAtlas/bin/Release/net8.0-windows/FanAtlas.exe --render 'Profil.cueprofile' 'Testausgabe'
```

`--self-test <Profil> <Bericht>` enthält zusätzlich Regressionstests gegen die Struktur des ursprünglichen Testprofils (13 Zuordnungen, 10 Geräte-IDs). Dieses private Profil wird nicht mitgeliefert. `--test-host <Profil> <Datenordner>` startet eine isolierte Testinstanz auf Port 17655. Das Plugin kann für lokale Tests `FANATLAS_TEST_BRIDGE` als abweichenden Pfad zu einer Bridge-Datei erhalten.

## Prüfung dieser Version

Build ohne Warnungen; Parser-, CSV-, Kurven- und Layouttests; fünf gerenderte App-Ansichten; lokale HTTP-Verbindung inklusive fehlender/falscher Schlüssel, fremdem Ursprung, ungültigen Kurven und Dateipfaden geprüft. Stream-Deck-WebSocket-Protokoll mit simuliertem Host geprüft: Registrierung, Tastenbilder, Auswahllisten, Wertewechsel und Kurvenauswahl. Elgato-Validierung und Paketierung erfolgreich. Die physische Tastenbelegung wird vom Benutzer vorgenommen.

## Bilder und Abhängigkeiten

Die Bilder wurden vom Auftraggeber zur Integration bereitgestellt. Marken und Bilder gehören ihren jeweiligen Rechteinhabern; daraus wird keine allgemeine Bildlizenz abgeleitet. Das Beispielplugin „Windows Utils“ wurde nur zur Orientierung verwendet; sein Programmcode und seine Binärdateien sind nicht Bestandteil dieses Projekts. .NET wird unter seinen jeweiligen MIT-/Drittanbieterlizenzen mitgeliefert. Elgato Stream Deck und Corsair iCUE sind separate Produkte.

Technische Referenzen: [Elgato Manifest](https://docs.elgato.com/streamdeck/sdk/references/manifest/), [Plugin-WebSocket](https://docs.elgato.com/streamdeck/sdk/references/websocket/plugin/), [Inno Setup](https://jrsoftware.org/isinfo.php).