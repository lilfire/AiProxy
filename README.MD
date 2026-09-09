# AiProxy

AiProxy er en lokal, OpenAI-kompatibel HTTP-proxy som samler flere AI-providers bak ett API. Tjenesten støtter Aigravity, Grok, Claude, Codex og Microsoft 365 Copilot, og har et lokalt administrasjonsgrensesnitt for providers, modeller, kvoter, sesjoner og logger.

Prosjektet er bygget med ASP.NET Core og .NET 9. Standardadressen er `http://127.0.0.1:3489`.

## Funksjoner

- OpenAI-kompatibelt Chat Completions API
- OpenAI-kompatibelt Responses API
- Streaming med Server-Sent Events
- Automatisk modelloppdagelse og provider-ruting
- Konfigurerbare modellalias
- Sesjonsgjenbruk mot provider-CLI-er
- Function tools og custom tools for M365 Copilot
- TodoWrite-integrasjon for OpenCode
- Bildeinput for Codex
- Administrasjon av providers og modeller uten omstart
- Visning av abonnementskvoter og lokal providerbruk
- Sesjonshistorikk og logger i minnet
- Swagger UI i Development-miljø

## Støttede providers

| Provider | Integrasjon | Modelloppdagelse | Bildeinput |
| --- | --- | --- | --- |
| Aigravity | `agy` CLI | Dynamisk via `agy models` | Nei |
| Grok | `grok` CLI | Dynamisk via `grok models` | Nei |
| Claude | `claude` CLI | Innebygd modelliste | Nei |
| Codex | `codex` CLI | Innebygd modelliste | Ja |
| M365 | Microsoft 365 Copilot ChatHub | Innebygd modelliste | Nei |

AiProxy bruker eksisterende lokal innlogging for de installerte provider-CLI-ene. M365-provideren bruker en lokal MSAL-token-cache eller en kompatibel token-cache fra `m365-copilot-cli`.

## Forutsetninger

- Windows
- .NET 9 SDK
- Minst én støttet provider installert og innlogget
- Microsoft 365 Copilot-lisens for bruk av M365-provideren
- Playwright Chromium dersom interaktiv M365-innlogging skal brukes

Provider-kommandoene må finnes på `PATH` eller i en av mappene AiProxy undersøker automatisk:

- `%USERPROFILE%\.local\bin`
- `%USERPROFILE%\.grok\bin`
- `%LOCALAPPDATA%\agy\bin`
- `%APPDATA%\npm`
- `%LOCALAPPDATA%\Microsoft\WindowsApps`

## Kom i gang

Gjenopprett avhengighetene og bygg løsningen:

```powershell
dotnet restore AiProxy.slnx
dotnet build AiProxy.slnx
```

Start tjenesten:

```powershell
dotnet run --project AiProxy/AiProxy.csproj
```

Følgende adresser blir da tilgjengelige:

- Administrasjon: `http://127.0.0.1:3489/admin`
- Health check: `http://127.0.0.1:3489/health`
- Swagger UI i Development: `http://127.0.0.1:3489/swagger`

Visual Studio-profilen åpner administrasjonssiden automatisk og setter `ASPNETCORE_ENVIRONMENT` til `Development`.

## API

AiProxy eksponerer disse offentlige endepunktene:

- `GET /v1/models`
- `POST /v1/chat/completions`
- `POST /v1/responses`
- `GET /health`

### Hent tilgjengelige modeller

```powershell
Invoke-RestMethod http://127.0.0.1:3489/v1/models
```

Modeller eksponeres normalt som `<Provider>-<modell>`, for eksempel `Claude-sonnet`. Hvis modellen har et konfigurert alias, brukes aliaset som synlig modell-ID.

### Chat Completions

```powershell
$body = @{
    model = "Claude-sonnet"
    stream = $false
    messages = @(
        @{
            role = "user"
            content = "Forklar prosjektet kort."
        }
    )
} | ConvertTo-Json -Depth 10

Invoke-RestMethod `
    -Method Post `
    -Uri http://127.0.0.1:3489/v1/chat/completions `
    -ContentType "application/json" `
    -Body $body
```

Sett `stream` til `true` for å få en OpenAI-kompatibel SSE-strøm som avsluttes med `data: [DONE]`.

### Responses API

```powershell
$body = @{
    model = "Codex-gpt-5.5"
    input = "Oppsummer arkitekturen."
    stream = $false
} | ConvertTo-Json -Depth 10

Invoke-RestMethod `
    -Method Post `
    -Uri http://127.0.0.1:3489/v1/responses `
    -ContentType "application/json" `
    -Body $body
```

Responses API godtar strenginput eller OpenAI-kompatible input-elementer. Ved streaming sendes blant annet følgende hendelser:

- `response.created`
- `response.in_progress`
- `response.output_text.delta`
- `response.output_text.done`
- `response.output_item.done`
- `response.completed`

## Sesjoner

AiProxy gjenbruker provider-sesjoner når klienten sender en stabil sesjons-ID. Følgende headere støttes, i prioritert rekkefølge:

1. `X-ShellAi-Session`
2. `X-Session-Id`
3. `X-Session-Affinity`

Eksempel:

```powershell
Invoke-RestMethod `
    -Method Post `
    -Uri http://127.0.0.1:3489/v1/responses `
    -Headers @{ "X-Session-Id" = "mitt-prosjekt" } `
    -ContentType "application/json" `
    -Body $body
```

Hvis ingen sesjonsheader sendes, opprettes en isolert `request-...`-ID. Dersom en provider-sesjon ikke lenger finnes, forsøker AiProxy å opprette en ny sesjon.

Sesjonshistorikken lagres bare i minnet og slettes når AiProxy startes på nytt.

## Modellruting

En modell løses i følgende rekkefølge:

1. Konfigurert modellalias
2. Eksplisitt provider-prefiks, for eksempel `Claude-sonnet`
3. Eksakt treff i modellene som providerne rapporterer
4. Innebygd prefiks for `grok-`, `claude-`, `codex-` eller `m365-`

Deaktiverte providers og modeller blir ikke brukt. Modellister caches i antall sekunder angitt av `CacheTtlSeconds`.

## Bildeinput

Chat Completions og Responses API kan lese multimodale `content`-arrayer med `image_url` eller `input_image`. Bare Codex-provideren annonserer støtte for bilder.

Følgende begrensninger gjelder:

- PNG, JPEG og WebP støttes
- Maksimal størrelse per bilde er 10 MB
- Maksimal samlet størrelse per forespørsel er 20 MB
- Kilden må være en base64-kodet `data:`-URL eller offentlig HTTPS-URL
- Maksimalt tre HTTP-videresendinger følges
- Lokale, private og reserverte nettadresser avvises

Nedlastede bilder lagres midlertidig og slettes etter provider-kallet.

## TodoWrite og verktøykall

Når en Responses-klient deklarerer `todowrite` eller `todo_write`, kan AiProxy videresende providerens oppgaveliste som et standard OpenAI `function_call`.

Claude sine TodoWrite-kall leses direkte fra `stream-json`. Providere uten et eget TodoWrite-verktøy kan bruke en intern kontrollprotokoll. Kontrollinnholdet fjernes fra teksten som klienten mottar.

M365-provideren kan oversette klientdeklarerte function tools og custom tools til standard OpenAI-responser. Klienten, ikke AiProxy, utfører det returnerte verktøykallet.

TodoWrite-broen kan deaktiveres med `TodoBridgeEnabled`.

## M365 Copilot

M365-provideren kobler til Microsoft 365 Copilot ChatHub over WebSocket. Følgende modell-ID-er er innebygd:

- `auto`
- `quick`
- `think-deeper`
- `gpt-5.6-think-deeper`
- `gpt-5.5-quick`

Interaktiv innlogging kan aktiveres fra `/admin/m365`. Token-cachen lagres som standard under `%LOCALAPPDATA%\AiProxy`.

Hvis en gyldig kompatibel cache finnes under `%USERPROFILE%\.config\opencode-m365\msal-cache.json`, kan denne gjenbrukes.

M365 kan få et avgrenset snapshot av en lokal prosjektmappe. Snapshotet utelater blant annet:

- Hemmelige filer som `.env`, nøkler og sertifikater
- Build-mapper som `bin`, `obj`, `dist` og `node_modules`
- Binærfiler og Office-dokumenter
- Filer større enn 300 KB
- Innhold utover en samlet grense på 1,5 MB

Direkte skriving fra modelltekst er deaktivert som standard. Når klientverktøy er deklarert, behandles workspace-snapshotet som skrivebeskyttet og filendringer skal utføres gjennom klientens skriveverktøy.

## Konfigurasjon

Standardinnstillingene ligger under `AiProxy` i `AiProxy/appsettings.json`:

```json
{
  "AiProxy": {
    "Host": "127.0.0.1",
    "Port": 3489,
    "TimeoutSeconds": 300,
    "TodoBridgeEnabled": true,
    "M365Copilot": {
      "Enabled": true,
      "EnableInteractiveLogin": false,
      "Locale": "en-GB",
      "TimeZone": "Europe/Oslo",
      "IncludeWorkspaceFiles": true,
      "EnableWorkspaceWrites": false
    }
  }
}
```

Runtime-innstillinger fra administrasjonsgrensesnittet lagres i `AiProxy/aiproxy.admin.json`. De omfatter:

- Timeout mellom 5 og 3600 sekunder
- Modellcache mellom 0 og 3600 sekunder
- Aktivering av TodoWrite-broen
- Aktive providers
- Synlige modeller og alias
- M365-innlogging og workspace-innstillinger

Endringer lagret i administrasjonsgrensesnittet gjelder for nye forespørsler uten omstart. Modellcachen tømmes når innstillingene endres.

`AllowedOrigins` kan konfigureres under `AiProxy` for eksplisitt CORS-tilgang. I Development tillates alle origins når listen er tom.

## Administrasjonsgrensesnitt

Administrasjonen under `/admin` er begrenset til loopback-adresser og inneholder:

- Oversikt over providers, modeller, bruk og kvoter
- Aktivering og deaktivering av providers
- Modellkatalog med synlighet og alias
- Runtime-innstillinger
- M365-innlogging og workspace-konfigurasjon
- Sesjonshistorikk fra aktiv prosess
- Live logger med opptil 1000 hendelser

Administrative API-er finnes under `/admin/api` for blant annet innstillinger, providerstatus, M365-status, sesjoner og SSE-strømmer for logger og sesjonsendringer.

## Tester

Kjør alle MSTest-testene:

```powershell
dotnet test AiProxy.slnx
```

Testene dekker blant annet:

- Modellruting og modellalias
- Providerargumenter og sesjonsgjenbruk
- Chat Completions og Responses API
- SSE-streaming
- Bildevalidering og opprydding
- TodoWrite-broen
- M365-verktøyprotokollen
- Workspace-filtrering og skriving
- Providerkvoter
- Feilhåndtering

Testmetodene kjører parallelt.

## Prosjektstruktur

```text
AiProxy.slnx
|- AiProxy/
|  |- Application/      Applikasjonstjenester og grensesnitt
|  |- Configuration/    Standard- og runtime-innstillinger
|  |- Contracts/        OpenAI-kompatible kontrakter
|  |- Pages/            Razor Pages-administrasjon
|  |- Routes/           API- og admin-endepunkter
|  |- Services/         Providers, sesjoner, logging og prosesskjøring
|  `- Validators/       FluentValidation-validatorer
`- AiProxy.Tests/       MSTest-enhetstester
```

## Sikkerhet og drift

- Tjenesten binder som standard bare til `127.0.0.1`.
- Administrasjonsgrensesnittet avviser ikke-lokale klienter.
- API-et har ingen egen autentisering. Ikke eksponer tjenesten offentlig uten et autentiserende reverse proxy-lag.
- M365-workspace kan sende innholdet i inkluderte tekstfiler til Microsoft 365 Copilot.
- Workspace-skriving er deaktivert som standard.
- Bilde-URL-er kontrolleres mot private og reserverte adresser for å redusere SSRF-risiko.
- Logger og sesjonshistorikk kan inneholde prompts og modellsvar, men lagres bare i minnet.
- Claude startes med `--dangerously-skip-permissions`. Kjør derfor AiProxy med minst mulige lokale rettigheter.

## Feilsøking

### Provider vises som utilgjengelig

Kontroller at providerens CLI er installert, innlogget og tilgjengelig på `PATH`. Providerstatusen under `/admin/providers` viser hvilken kommando AiProxy ikke finner.

### Ingen modeller vises

Kontroller at provideren er aktivert. Bruk deretter `Oppdater oppdagelse` under `/admin/models`.

Aigravity og Grok henter modellene fra CLI-en. Claude, Codex og M365 bruker innebygde modellister.

### M365 ber om innlogging

Aktiver interaktiv innlogging under `/admin/m365`, start tjenesten fra en interaktiv Windows-sesjon og fullfør innloggingen i Chromium-vinduet.

### Forespørsler mister samtalekontekst

Send samme `X-ShellAi-Session`, `X-Session-Id` eller `X-Session-Affinity` på alle turene i samtalen.

### Rate limit

Når en provider rapporterer en tolkbar kvotefeil, returnerer AiProxy HTTP `429` med `Retry-After` og feilkoden `rate_limit_exceeded`.