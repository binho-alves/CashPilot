# Phase 4 — phone app (Android) over the home network

## Decisions
- **One user, near-zero cost.** SQLite stays; the database stays on the owner's PC. No cloud, no Postgres.
- **Phone: .NET MAUI (Android).** The phone has no SIM card and only joins the home Wi-Fi, so it reaches the PC by its LAN address. Away from home it is offline: entries go into a local queue and are sent when the phone is back on the home network. Tailscale is not needed (and can be added later without changing the app: only the address changes).
- **Scope: entries only.** New entry (expense, income, card-bill payment, transfer between own accounts), last entries (with delete for the ones typed by hand) and settings. Reports stay on the web pages.
- **API inside `CashPilot.Web`**, under `/api/v1`, protected by the `X-Api-Key` header (constant-time comparison). With no `CashPilot:ApiKey` configured the API answers 503.
- **The web pages only answer to this computer.** Any request that is not from loopback and not under `/api/v1` gets 403 (set `CashPilot:AllowRemoteUi=true` to open the pages to the network on purpose). So listening on the LAN exposes only the keyed API.
- **No duplicates, ever.** The phone creates a `clientId` (GUID) when the entry is typed. It becomes the entry's id and dedup key (`MANUAL-<id>`), so a retry after a timeout returns the stored entry (`200`, `alreadyExisted: true`) and a deleted entry is not brought back by a late retry.
- **Only hand-typed entries are deletable from the phone** (`source = 'manual'`); imported history cannot be erased by a tap.
- **Shared contracts** live in `CashPilot.Contracts` (plain records + `MoneyInput`), used by the server and the app. The logic is in `Infrastructure/Mobile/MobileEntryService` and is unit-tested; the endpoints are thin.
- **MAUI project outside `CashPilot.slnx`**, so `dotnet build/test` of the solution never needs the MAUI workload.

## API (`/api/v1`, header `X-Api-Key`)
| Method | Path | Meaning |
|---|---|---|
| GET | `/ping` | Key check |
| GET | `/lookups` | Accounts (name, kind) and known category/item pairs |
| GET | `/entries?limit=30` | Entries typed by hand, newest first |
| POST | `/entries` | `NewEntryRequest` → `201` created / `200` already existed / `400` refused (`{error}`) |
| DELETE | `/entries/{id}` | `204`, or `404` when the entry is not a live hand-typed one |

`kind`: `expense`, `income`, `billPayment`, `transferOut`, `transferIn`. `amount` is always positive.

## Set-up, step by step

### 1. On the PC
1. Create the key (any long random text) and store it outside the repository:
   ```
   cd C:\dev\CashPilot\src\CashPilot.Web
   dotnet user-secrets set "CashPilot:ApiKey" "<long random text>"
   ```
2. Let Windows accept connections on the private (home) network, once, in an administrator PowerShell:
   ```
   New-NetFirewallRule -DisplayName "CashPilot (rede local)" -Direction Inbound -Protocol TCP -LocalPort 5080 -Action Allow -Profile Private
   ```
   (the Wi-Fi/network must be set to "Private" in Windows).
3. Give the PC a fixed address: in the router, reserve the PC's IP (DHCP reservation), e.g. `192.168.0.10`. Find the current one with `ipconfig`.
4. Start the app listening on the network, with the launch profile "CashPilot.Web (rede local)":
   ```
   dotnet run --project src/CashPilot.Web --launch-profile "CashPilot.Web (rede local)"
   ```
   The pages still open only at `http://localhost:5080` on the PC.
5. Check from another device on the Wi-Fi: `http://192.168.0.10:5080/api/v1/ping` must answer 401 (the key is missing) — that means it is reachable.

### 2. Build and install the app (once, on the PC)
```
dotnet workload install maui-android
dotnet build src/CashPilot.Mobile -f net10.0-android -c Release
```
Needs the Android SDK/JDK (Visual Studio's "Mobile development with .NET" installs them). Enable developer options and USB debugging on the phone, plug it in and run `dotnet build src/CashPilot.Mobile -f net10.0-android -t:Run` — or copy the signed `.apk` from `bin/Release/net10.0-android/` to the phone and open it.

### 3. In the app
Config. tab → address `http://192.168.0.10:5080`, the same API key → "Salvar e testar conexão". It downloads the accounts and categories; after that you can type entries even without the PC.

## Known limits
- Plain HTTP on the home network (private address, no certificate). Do not expose port 5080 to the internet.
- If the PC is off or the phone is away, entries wait in the queue (`pending.json` in the app's private storage). Uninstalling the app loses the queue.
- Accounts and categories on the phone are the last ones downloaded; a new account created on the PC appears after the next sync.
