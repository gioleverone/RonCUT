# Contribuire a RonCUT

RonCUT usa la licenza GNU GPL versione 3 (`GPL-3.0-only`), disponibile in [LICENSE](LICENSE). I contributi al progetto devono poter essere distribuiti con questa licenza. Conservare gli avvisi delle dipendenze e indicare provenienza e licenza di eventuale materiale di terzi.

Per segnalare un problema, indicare versione di RonCUT, versione di Windows, passi per riprodurlo e risultato atteso. Se possibile allegare un piccolo SVG o progetto `.roncut` riproducibile, privo di informazioni personali e del quale si possiedano i diritti di condivisione.

Non pubblicare URL API completi di token, impostazioni personali o credenziali. Specificare se il problema è stato osservato in simulazione o su hardware reale. Per i problemi GRBL indicare firmware e contesto della prova, oscurando eventuali dati privati.

## Preparare una modifica

- Usare C# e WPF/.NET 8, mantenendo lo stile dei file esistenti e le graffe su una nuova riga.
- Eseguire `Compila-RonCUT.ps1` da Windows e riportare l'esito nella pull request.
- Per modifiche al CAM, al protocollo GRBL o ai movimenti, aggiungere verifiche pertinenti usando trasporti simulati. Distinguere sempre questi risultati dalle prove sulla CNC reale.
- Non automatizzare il desktop né inviare comandi a una CNC reale come parte della suite.
- Conservare CUT con orientamento dall'alto coerente con DESIGN e le viste Sopra/Assonometria.
- Distribuire un solo `RonCUT.exe` autonomo con nome stabile; non aggiungere BAT di avvio, cartelle numerate o copie di vecchie versioni.

Le pull request devono descrivere il problema, il comportamento dopo la modifica e le verifiche eseguite. Conservare gli originali dei media in `data` quando si aggiungono risorse ottimizzate all'applicazione, e documentarne provenienza e licenza.
