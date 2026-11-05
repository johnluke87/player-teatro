# Cue Player

Lettore di musiche per spettacoli teatrali, per Windows 10/11. Un unico `CuePlayer.exe`,
nessuna installazione, funziona anche da chiavetta USB e senza internet.

**Manuale completo: [docs/Manuale-CuePlayer.pdf](docs/Manuale-CuePlayer.pdf)**

## In breve

- **Apri cartella**: tutte le musiche della cartella (MP3, WAV, M4A/AAC, FLAC, WMA, AIFF) compaiono in ordine di nome
  e vengono caricate in memoria, quindi partono all'istante.
- **Ogni traccia**: ▶/⏸, ⏹ stop, ⏮ da capo, **SFUMA** (durata a scelta), volume, 🔁 loop, **EQ** bassi/medi/alti.
- **COMANDI**: pulsanti che crei tu. Per ogni traccia scegli cosa fa quando premi il pulsante:
  ▶ **Parte**, 🔉 **Volume** (abbassa o riporta su una traccia che suona), ⏸ **Pausa**, ⏹ **Stop**, con volume e tempo del passaggio.
  Si lanciano con un clic, con i tasti 1-9 o con **GO** (Spazio), che li esegue in ordine.
- In basso: **GO**, **SFUMA TUTTO**, **STOP TUTTO**, **EQ generale** a 5 bande, volume generale con indicatore di livello.
- Se l'uscita audio scelta non è disponibile, passa da sola a un'altra uscita funzionante.
- Tutto viene salvato da solo in `cueplayer.json` dentro la cartella delle musiche.

Esempio:

| Pulsante | Cosa fa |
|---|---|
| 1 · Parte MusicaA | MusicaA ▶ Parte 100% subito |
| 2 · Entra MusicaB, MusicaA sotto | MusicaB ▶ Parte 100% subito · MusicaA 🔉 Volume 20% in 2 s |
| 3 · Via MusicaB, torna MusicaA | MusicaB ⏹ Stop subito · MusicaA 🔉 Volume 100% in 2 s |
| 4 · Dissolvenza MusicaA → MusicaC | MusicaA ⏹ Stop in 3 s · MusicaC ▶ Parte 100% in 3 s |

| Tasto | Azione |
|---|---|
| Spazio | GO (prossimo comando) |
| 1 … 9 | esegue il comando con quel numero |
| ↑ / ↓ | cambia il prossimo comando |
| F | sfuma tutto |
| Esc | stop tutto |

## Sorgenti

`sorgenti/`: C# .NET 8, WPF + [NAudio](https://github.com/naudio/NAudio) (WASAPI). Mixer sempre attivo con decodifica
in memoria, sfumate a potenza costante, EQ biquad (RBJ).

```
dotnet test sorgenti/CuePlayer.Tests
dotnet publish sorgenti/CuePlayer/CuePlayer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

Il manuale si rigenera da `docs/manuale/manuale.html` (Edge/Chrome → Stampa → PDF).
