# CurlGrabber

Ein kleines Windows-Frontend für `curl.exe`: cURL-Befehl aus Firefox einfügen, Dateinamen und
Zielordner wählen, herunterladen — mit Fortschrittsbalken, Tempo und Restzeit im Fenster.

Gedacht für Videos hinter kurzlebigen CDN-Links, bei denen der Download nur mit den
Original-Headern der Browser-Sitzung funktioniert.

## Herunterladen

Fertige Pakete liegen unter [Releases](https://github.com/HannaPanda/CurlGrabber/releases):

| Datei | |
| --- | --- |
| `CurlGrabber-vX.Y.Z-win-x64-standalone.zip` | Eine einzelne EXE, läuft sofort. ~100 MB, weil die .NET-Runtime eingebettet ist. |
| `CurlGrabber-vX.Y.Z-win-x64.zip` | Nur 0,1 MB, setzt aber die [.NET-10-Desktop-Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) voraus. |

Im Zweifel die Standalone-Variante nehmen.

## Verwendung

1. In Firefox <kbd>F12</kbd> → Reiter **Netzwerkanalyse** → die gewünschte Anfrage suchen
2. Rechtsklick → **Kopieren** → **Als cURL kopieren**
3. In CurlGrabber auf **Aus Zwischenablage einfügen**
4. Dateinamen prüfen (wird aus der URL vorgeschlagen, sonst `video.mp4`)
5. Zielordner wählen — beim ersten Start `E:\Movies`, danach der zuletzt benutzte
6. **Download starten**

Beide Varianten aus dem Firefox-Menü funktionieren: *Als cURL kopieren (Windows)* mit
`^`-Maskierung und die POSIX-Variante mit `\` und `'`. Auch Chrome-Befehle lassen sich einfügen.

## Optionen

| Option | Wirkung |
| --- | --- |
| Fortsetzen (`-C -`) | Setzt einen abgebrochenen Download an der vorhandenen Stelle fort |
| Wiederholen (`--retry`) | 5 Versuche mit 2 s Pause bei Netzwerkfehlern |
| Bei HTTP-Fehler abbrechen (`--fail`) | Verhindert, dass eine Fehlerseite als Videodatei gespeichert wird |

Ist die Zieldatei bereits vorhanden, fragt CurlGrabber nach: neu herunterladen oder fortsetzen.

## Wie es arbeitet

Der eingefügte Befehl wird zerlegt statt weitergereicht. URL, alle `-H`-Header, Cookies und
Daten werden übernommen; `-o`, `-O`, `-s` und `-#` werden verworfen, weil CurlGrabber Ziel und
Fortschrittsanzeige selbst steuert.

Auch `Range`-Angaben werden verworfen — sowohl der `-H "Range: bytes=…"`-Header als auch
`-r`/`--range`. Ein Videoplayer lädt immer nur den Ausschnitt, den er gerade abspielt, und
genau dieser Ausschnitt steht im kopierten Befehl. Ohne das Entfernen käme statt der Datei nur
ein paar Kilobyte großes Stück an; einige CDNs antworten dabei mit `200` statt `206`, sodass
curl die Kürzung nicht bemerkt und die Teildatei als fertigen Download meldet. Was entfernt
wurde, steht neben den Buttons und noch einmal im Log.

Aufgerufen wird anschließend das mit Windows ausgelieferte `C:\Windows\System32\curl.exe` — jedes
Argument einzeln über `ProcessStartInfo.ArgumentList`, ohne Umweg über `cmd.exe`. Dadurch gibt es
keine Escaping-Probleme, egal welche Sonderzeichen in den Headern stehen.

Die Fortschrittsanzeige liest curls Statustabelle von stderr. curl trennt diese Zeilen mit
Wagenrücklauf statt Zeilenumbruch und lässt die Zeitspalten leer, solange die Werte unbekannt
sind — deshalb wird der Strom zeichenweise gelesen und nur die stets belegten Spalten
ausgewertet; die Restzeit rechnet CurlGrabber selbst aus.

## Häufige Fehlermeldungen

**Exitcode 22** — der Server hat mit 403 oder 404 geantwortet. Bei CDN-Links ist fast immer der
Token in der URL abgelaufen; einfach den cURL-Befehl in Firefox neu kopieren.

**Exitcode 33 oder 36** — der Server unterstützt kein Fortsetzen. Option *Fortsetzen* abschalten
und neu starten.

## Bauen

Voraussetzung ist das .NET-10-SDK.

```powershell
.\build.ps1
```

Beide ZIP-Varianten landen unter `dist\`. Mit `-Release` werden sie zusätzlich als
GitHub-Release hochgeladen; die Versionsnummer stammt aus der `.csproj`.

`PublishSingleFile` kommt bewusst nur bei der Standalone-Variante zum Einsatz — zusammen mit
`SelfContained=false` bettet das SDK die Runtime trotzdem ein und die EXE wächst auf über
100 MB, statt die erwarteten 0,2 MB zu bleiben.

## Einstellungen

Zuletzt benutzter Ordner, Fenstergröße und Optionen liegen in
`%APPDATA%\CurlGrabber\settings.json`.

## Lizenz

MIT
