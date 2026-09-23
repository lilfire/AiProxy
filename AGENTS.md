# Agentinstruksjoner for AiProxy

## Testing

- All testing som validerer integrasjonen skal gjennomføres ende-til-ende med `opencode`.
- Ikke betrakt en test som fullført før den relevante ende-til-ende-testen med `opencode` har kjørt og resultatet er kontrollert.

## Opprydding etter testing

- Stopp alltid alle prosesser som ble startet for testing når testen er ferdig, enten den besto eller feilet.
- Bekreft at prosessene faktisk er avsluttet før du rapporterer testresultatet.
- Unngå å stoppe prosesser som ikke ble startet som del av den aktuelle testkjøringen.
