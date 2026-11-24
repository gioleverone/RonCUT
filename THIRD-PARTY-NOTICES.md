# Dipendenze di RonCUT

RonCUT usa la licenza GNU GPL versione 3 (`GPL-3.0-only`), riportata in [LICENSE](LICENSE). Le librerie esterne conservano le proprie licenze. Le versioni dirette qui elencate corrispondono a `RonCUT/RonCUT.csproj`.

| Componente | Versione | Autore | Licenza |
| --- | --- | --- | --- |
| [Clipper2](https://github.com/AngusJohnson/Clipper2) | 2.0.0 | Angus Johnson, copyright 2010–2025 | Boost Software License 1.0 |
| [System.IO.Ports](https://github.com/dotnet/runtime) | 8.0.0 | Microsoft / .NET Foundation and Contributors | MIT |
| [.NET e WPF](https://github.com/dotnet) | Runtime .NET 8 selezionato dall'SDK alla pubblicazione | Microsoft / .NET Foundation and Contributors | Licenze e avvisi dei componenti .NET |

I testi di licenza di Clipper2 e System.IO.Ports e gli avvisi del pacchetto System.IO.Ports sono conservati in [docs/third-party](docs/third-party/), copiati dai pacchetti NuGet delle versioni indicate.

La pubblicazione autonoma include il runtime .NET. Prima di distribuire una release binaria, includere anche le licenze e gli avvisi della versione effettiva del runtime usato: [licenza .NET Runtime](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT), [avvisi .NET Runtime](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT), [licenza WPF](https://github.com/dotnet/wpf/blob/main/LICENSE.TXT) e [avvisi WPF](https://github.com/dotnet/wpf/blob/main/THIRD-PARTY-NOTICES.TXT). I testi nei pacchetti distribuiti restano il riferimento per la versione specifica.
