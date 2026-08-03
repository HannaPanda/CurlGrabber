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
| Vorgetäuschten Datei-Anfang entfernen | Schneidet Tarnbytes vor der eigentlichen Nutzlast weg (siehe unten) |
| In Abschnitten laden | Holt die Datei in mehreren Anfragen, bis nichts Neues mehr kommt (siehe unten) |
| Danach nach MP4 umpacken | Legt den fertigen Transportstrom verlustfrei in einen MP4-Container (siehe unten) |

Ist die eingefügte URL eine HLS-Playlist, holt CurlGrabber alle darin genannten Stücke und setzt
sie zusammen — siehe [Playlists](#playlists-hls--m3u8).

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

## Playlists (HLS / m3u8)

Steckt hinter der URL keine Videodatei, sondern eine `#EXTM3U`-Playlist, lädt CurlGrabber alles
nach, was darin steht, und setzt es in Playlist-Reihenfolge zusammen. Erkannt wird das am Inhalt,
nicht an der Dateiendung — diese Hoster liefern die Playlist gern mit falschem Content-Type aus
(gesehen: `image/jpeg`).

Ist es eine Master-Playlist, wird automatisch die Variante mit der höchsten `BANDWIDTH` genommen
und deren Segmentliste geladen.

### Getrennte Tonspur

Führt die Master-Playlist den Ton als eigene Spur (`EXT-X-MEDIA:TYPE=AUDIO` mit `URI`), enthält
die Bild-Variante wirklich nur Bild — für sich genommen ein Stummfilm. CurlGrabber lädt dann beide
Listen und fügt Bild und Ton hinterher wieder zusammen. Gibt es mehrere Tonspuren, gewinnt die mit
`DEFAULT=YES`, sonst die erste; Untertitelspuren bleiben liegen.

Das Zusammenfügen erledigt ffmpeg. Fehlt es, bleiben beide Teile als `name.ts` und `name.ton.ts`
nebeneinander liegen — mit einem deutlichen Hinweis im Log statt eines stummen Films.

### Viele kleine Segmente

Zwei-Sekunden-Segmente sind verbreitet, und dann stehen für einen Spielfilm schnell ein paar
tausend Einträge in der Liste. Ein curl-Aufruf je Segment kostet dabei mehr Zeit im Prozessstart
und TLS-Handschlag als in der Übertragung. Deshalb packt CurlGrabber je **24 Segmente in einen
Aufruf** und lässt curl davon **6 gleichzeitig** holen. Gemessen an zwanzig Segmenten desselben
Hosters: **3,9 s einzeln, 0,53 s gebündelt**, bei Byte für Byte gleichem Ergebnis.

Angehängt wird trotzdem streng in Playlist-Reihenfolge, unabhängig davon, welche Anfrage zuerst
fertig war. Gebündelt wird nur, wenn jedes Stück eine ganze Datei ist — sobald Byte-Bereiche im
Spiel sind, braucht jedes Stück seinen eigenen `Range`-Header, und der gilt in curl für alle URLs
eines Aufrufs.

### Byte-Bereiche

Der andere interessante Fall ist `EXT-X-BYTERANGE`: dann liegen viele Segmente in wenigen großen
Dateien und werden nur über Byte-Bereiche angesprochen. CurlGrabber fasst aufeinanderfolgende
Bereiche derselben Datei wieder zu einem Abruf zusammen. Bei einem gemessenen Beispiel wurden aus
**1760 Segmenten 26 Abrufe** — statt 1760 Anfragen also 26, weil die Bereiche jeder Datei
lückenlos aneinander anschließen. Gibt es echte Lücken, bleiben es entsprechend mehrere Abrufe.

Der vorgetäuschte Datei-Anfang wird dabei je Datei einmal entfernt, und zwar nur bei Abrufen, die
bei Byte 0 beginnen — weiter hinten steht keiner.

Sind die Segmente verschlüsselt (`EXT-X-KEY` mit einer anderen Methode als `NONE`), bricht
CurlGrabber ab und sagt das, statt eine unbrauchbare Datei zu hinterlassen.

Als Dateiname wird bei einer `.m3u8`-URL `video.ts` vorgeschlagen — ebenso bei `.m3u` und `.txt`,
weil manche Hoster ihre Master-Playlist als `master.txt` ausliefern.

## Nach MP4 umpacken

HLS liefert MPEG-Transportströme. Das ist ein anderer Container als MP4, auch wenn viele Player
den Unterschied nicht zeigen — sie erkennen den Inhalt und ignorieren die Dateiendung, weshalb ein
Transportstrom mit `.mp4` im Namen scheinbar problemlos läuft.

Ist die Option an und liegt ffmpeg neben `CurlGrabber.exe` oder im `PATH`, wird der fertige Strom
anschließend umgepackt:

```
ffmpeg -i video.ts -map 0:v:0 -map 0:a:0 -c copy -movflags +faststart video.mp4
```

`-c copy` heißt: Bild und Ton werden Byte für Byte übernommen, es wechselt nur die Verpackung.
Das dauert Sekunden statt Stunden und verliert nichts. Gemessen an einem zweistündigen Film:
**3 Sekunden**, dabei 393 MB → 362 MB, weil der TS-Overhead wegfällt — 4 Byte Kopf je 188-Byte-Paket
plus Programmtabellen und Füllbytes. `+faststart` zieht die Sprungtabelle nach vorne, damit sich
die Datei sofort abspielen und durchsuchen lässt.

Wird ffmpeg nicht gefunden, bleibt es beim `.ts` und im Log steht, warum. Nur bei getrennter
Tonspur ist der Schritt keine Kür — dort wird er unabhängig von der Option versucht, weil das Bild
sonst stumm bliebe.

## In Abschnitten laden

Manche Hoster geben pro Antwort nur eine begrenzte Menge heraus. Weil sie dabei mit `200 OK`
antworten statt mit `206` und keinen `Content-Range` mitschicken, merkt curl davon nichts und
meldet einen erfolgreichen Download — die Datei ist aber stillschweigend abgeschnitten.

Ist die Option an, stellt CurlGrabber nach dem ersten Durchgang weitere Anfragen mit
`Range: bytes=…-`, bis ein Abschnitt nichts Neues mehr bringt. Die erste Anfrage bleibt bewusst
ohne `Range`, damit sich der Server genauso verhält wie beim Abspielen im Browser.

Zusammengesetzt wird **nicht nach Byte-Positionen, sondern über die Überlappung**: jede Anfrage
setzt 64 KiB vor dem bereits Vorhandenen an, und der neue Abschnitt wird dort angesetzt, wo sich
die letzten 4 KiB der vorhandenen Daten in ihm wiederfinden. Das ist nötig, weil Server den
angeforderten Start nicht zwingend einhalten — bei dem hier untersuchten CDN beginnt jede
Antwort 39 Bytes hinter dem angeforderten Offset. Nach Byte-Positionen aneinandergehängt fehlten
genau diese 39 Bytes an jeder Abschnittsgrenze.

Das Suchmuster ist absichtlich viel kürzer als die Rückgriffweite: beginnt der Server *später*
als angefordert, liegt der Anfang der Überlappung gar nicht im Abschnitt, und ein Muster über die
volle Überlappung wäre nicht mehr auffindbar. Schließt ein Abschnitt gar nicht an, bricht
CurlGrabber ab, statt eine falsch zusammengesetzte Datei zu hinterlassen.

Nebenbei ersetzt das auch das Fortsetzen: eine angefangene Datei wird über dieselbe Überlappung
weitergeführt, deshalb entfällt `-C -`, solange die Option an ist.

Bei einem Server ohne Deckel kostet das genau eine zusätzliche Anfrage, die dann nichts Neues
mehr liefert.

## Vorgetäuschter Datei-Anfang

Manche Hoster stellen ihren Video-Segmenten ein paar Bytes voran, die wie ein harmloses Asset
aussehen — mal ein PNG-Fragment, mal CSS, HTML oder eine WOFF-Signatur —, aufgefüllt bis zu einer
runden Länge. Die Nutzlast dahinter ist unverändert.

CurlGrabber erkennt nicht die Tarnung, sondern den Beginn der echten Datei: gesucht wird die
erste Stelle, ab der ein durchgehendes MPEG-TS-Paketraster (0x47 alle 188 Bytes, über 50 Pakete
geprüft, Paketköpfe auf Plausibilität) oder eine gültige ISO-BMFF-Boxkette (`ftyp`/`styp` mit
passender Folgebox) steht. Damit ist gleichgültig, was sich der Hoster als Tarnung ausdenkt.

Zwei Fallstricke sind dabei berücksichtigt:

- Das letzte Byte der Tarnung kann zufällig ein `0x47` sein und genau auf dem Paketraster liegen —
  `GIF89a` fängt zum Beispiel mit dem TS-Sync-Byte an. Der Fundort wäre dann ein Paket zu früh,
  deshalb wird zusätzlich auf die Programmtabelle (PAT auf PID 0) ausgerichtet, sofern eine in den
  nächsten acht Paketen steht.
- Findet sich gar keine bekannte Nutzlast, wird nichts angefasst. Zufallsdaten, echte PNGs,
  Textdateien und HTML-Fehlerseiten bleiben unverändert.

Geschnitten wird durch Vorschieben innerhalb der Datei, ohne zweite Kopie auf der Platte — bei
mehreren Gigabyte Video ist das der Unterschied zwischen „geht" und „kein Platz mehr".

Zu beachten: ffmpeg und VLC synchronisieren sich auch mit der Tarnung problemlos neu, der Schnitt
ist für sie kein Unterschied. Er lohnt für strengere Player, für die Dateityp-Erkennung und fürs
Weiterverarbeiten.

## Wie die Fortschrittsanzeige arbeitet

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
