# CashPilot — project context

Personal cash-flow control in C# (.NET 10). Owner: binho-alves. Language: reply in Portuguese; code, identifiers, commits and the main README in English (README.pt-BR.md mirrors it).
Category names and bank-statement patterns stay in Portuguese: they are data from Brazilian banks.

## Goal
Feed the app statements (CSV/OFX first; PDF/image later) from several accounts, credit cards, boletos and receivables. It must track account/card limits and due dates, hold per-account interest rules, and answer: "I need R$ X for N days — what is the cheapest way?"

## Domain rules (decided)
- Pix/TED between the owner's own accounts is an **internal transfer**: not spending, not income (see `TransferDetector`).
- Paying a credit card bill is **not** spending: the purchases are already on the card lines (see `CardBillPaymentDetector`). Only interest and fees on the bill are expenses.
- All interest rolls up into one category, "Juros e encargos", with a type (`InterestType`): overdraft (cheque especial/LIS, incl. IOF), late boleto, late card payment, card cash-out fee, bank fees.
- **Card cash-out via card terminal**: the card is charged the gross amount, the account receives net = gross − fee, the fee is deducted on the spot. 1x fee = 3.08% (R$ 1,000 → cost R$ 30.80, net R$ 969.20). Fees for 2x, 3x… are unknown yet; the user will supply them (`CashAdvanceFeeTable`). The expense is the fee only.
- **Classification learns**, in layers: exact normalized match → manual "contains" rule → trigram similarity ≥ 0.8 → unknown (ask the user, then `Learn`).
- **Imports are idempotent**: `Transaction.ComputeDedupKey(occurrence)`; same file twice must not duplicate. Installments (e.g. 03/06) are part of the key.
- Signed amounts: negative = outflow, positive = inflow.

## Source spreadsheet (the user's existing Google Sheets, exported)
Tabs: `Principal` (monthly pivot by category/item), `Gastos` (columns Dia, Categoria, Ítem, Conta, Valor, Obs; ~600 rows, values like "R$ 1.234,56", dates dd/MM/yyyy, includes future installments), `Cadastros1` (lists of categories, items and accounts), `Página3` (category → items matrix).
Known quirks: many rows have no category (marketplace/wallet purchases) → this is the initial training queue for the classifier; some installments repeat across months; "Cartão de Crédito" under Juros is card-bill interest, not the bill payment itself.
The real spreadsheet and statements contain personal data. This repo is **public**: never commit them (`/data/` is git-ignored), nor family names or account numbers. Use invented data in tests.

## Layout
- `src/CashPilot.Domain` — pure rules, no dependencies (Descriptions, Transactions, Interest)
- `tests/CashPilot.Domain.Tests` — xUnit (28 tests passing on the user's machine, .NET 10.0.12)

## Roadmap
0. Domain + tests — done
1. SQLite persistence + CSV/OFX importer; seed from the `Gastos` tab (next step)
2. Accounts, limits, card bills (closing/due dates), per-account interest rules
3. Payment calendar, cash-flow forecast, cost simulator (cash-out vs overdraft vs late boleto vs revolving credit)
4. C# API, then mobile (.NET MAUI or Flutter) on the same API; Postgres instead of SQLite

## Working notes
- The cloud sandbox that Claude runs in has no .NET SDK: the user runs `dotnet test` and reports results. Don't claim code was compiled unless it was.
- The sandbox cannot push to GitHub (no credentials): the user runs `git push` from Windows.
- Commits end with the Co-Authored-By / Claude-Session trailers.
