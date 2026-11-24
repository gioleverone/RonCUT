# RonCUT

RonCUT è un'applicazione Windows per preparare lavorazioni CNC a partire da SVG, generare G-code e controllare una scheda GRBL 1.1. È sviluppata in C# con WPF e .NET 8. L'interfaccia è in italiano.

Versione corrente: **1.5.2**. Il progetto è in sviluppo; le verifiche automatiche usano simulatori e non costituiscono una validazione sulla macchina reale.

## Funzioni

- **DESIGN**: importazione SVG, tagli esterni e interni, incisioni, fori, tasche con isole, passate e ponticelli.
- Archivio utensili e supporti, selezione multipla delle lavorazioni e pause per il cambio utensile.
- Progetti `.roncut` in JSON, con SVG incorporato, geometria, lavorazioni e parametri.
- Esportazione G-code per i progetti senza pause cambio utensile.
- **CUT**: anteprima dall'alto coerente con DESIGN, assonometria, simulatore e collegamento seriale GRBL 1.1.
- Movimenti manuali a passo o continui, profili Slow/Fast e STOP manuale dedicato.
- Notifiche facoltative di avvio e fine attraverso un URL API configurato localmente.

## Uso e simulazione

Il programma distribuito è un unico `RonCUT.exe` autonomo per Windows x64: non richiede l'installazione del runtime .NET. Gli aggiornamenti sostituiscono lo stesso file; chiudere RonCUT prima di aggiornarlo.

Per iniziare, aprire in DESIGN `Esempi/test-ponticelli.roncut`. Sono disponibili anche un esempio con curve e uno con una tasca e due isole. La guida completa è in [LEGGIMI RonCUT.txt](LEGGIMI%20RonCUT.txt).

**CUT tenta automaticamente il collegamento alla porta COM configurata.** Per provare il programma senza macchina, scollegare la CNC prima di entrare in CUT, poi scegliere **Avvia simulatore**. La scritta SIMULAZIONE identifica la modalità attiva.

Prima dell'uso reale occorre verificare sulla propria macchina dimensioni, origine, utensile, avanzamenti, giri mandrino e quota di sicurezza. I valori iniziali e gli esempi non sono parametri raccomandati. PAUSA può lasciare acceso il mandrino; STOP software non sostituisce l'arresto fisico. La simulazione non verifica collisioni, limiti, accelerazioni o comportamento reale del mandrino.

## Compilazione e verifiche

Occorrono Windows x64, PowerShell e l'SDK .NET 8 con supporto WPF. Non è necessario Visual Studio. NuGet ripristina le dipendenze da nuget.org tramite `NuGet.Config`.

Dalla cartella principale:

```powershell
.\Compila-RonCUT.ps1
```

Lo script compila in Release, esegue la suite automatica, scrive il risultato locale in `VERIFICHE.txt` e pubblica un unico `RonCUT.exe` nella cartella principale. Usa l'SDK in `%LOCALAPPDATA%\RonCUT-Dev\dotnet` se presente, altrimenti `dotnet` nel PATH. Se RonCUT è aperto, lo script richiede di chiuderlo prima di sostituire l'eseguibile.

Le prove seriali, tastiera e HTTP sono simulate. Alcuni test WPF creano finestre fuori schermo, senza automazione del desktop. Conservare i file di esempio e gli SVG inclusi in `data`: sono necessari alla suite.

## Struttura e dati personali

| Percorso | Contenuto |
| --- | --- |
| `RonCUT/` | Applicazione WPF, risorse e profilo di pubblicazione Windows |
| `RonCUT.Tests/` | Suite automatica software |
| `Esempi/` | Progetti di esempio e SVG usato dai test |
| `data/` | Originali selezionati necessari ai test e icona originale |
| `Compila-RonCUT.ps1` | Compilazione, verifiche e produzione dell'eseguibile |

Le impostazioni attive restano in `%LOCALAPPDATA%\RonCUT`. Non caricare su GitHub impostazioni personali, URL con token, credenziali, screenshot privati o file di lavorazioni personali. `.gitignore` esclude compilazioni, eseguibile, impostazioni trasferite, appunti locali e originali non selezionati.

## Contributi e dipendenze

Vedi [CONTRIBUTING.md](CONTRIBUTING.md) per proporre modifiche e [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) per le dipendenze e le rispettive licenze.

## Licenza

RonCUT è distribuito con licenza **GNU General Public License, versione 3** (`GPL-3.0-only`). Il testo completo è in [LICENSE](LICENSE).

È possibile usare, studiare, modificare e distribuire RonCUT, anche per uso commerciale, rispettando la GPLv3. Chi distribuisce versioni modificate deve rendere disponibile il sorgente corrispondente secondo le condizioni della stessa licenza. Il software è fornito senza garanzie, nei limiti previsti dalla licenza.

Le dipendenze mantengono le proprie licenze, elencate in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
