# CashPilot — project context

Personal cash-flow control in C# (.NET 10). Owner: binho-alves. Language: reply in Portuguese; code, identifiers, commits and the main README in English (README.pt-BR.md mirrors it).
Category names and bank-statement patterns stay in Portuguese: they are data from Brazilian banks.

## Goal
Feed the app statements (CSV/OFX first; PDF/image later) from several accounts, credit cards, boletos and receivables. It must track account/card limits and due dates, hold per-account interest rules, and answer: "I need R$ X for N days — what is the cheapest way?"

## Domain rules (decided)
- Pix/TED between the owner's own accounts is an **internal transfer**: not spending, not income (see `TransferDetector`).
- Paying a credit card bill is **not** spending: the purchases are already on the card lines (see `CardBillPaymentDetector`). Only interest and fees on the bill are expenses.
- All interest rolls up into one category, "Juros e encargos", with a type (`InterestType`): overdraft (cheque especial/LIS, incl. IOF), late boleto, late card payment, card cash-out fee, bank fees.
- **Card cash-out via card terminal**: the card is charged the gross amount, the account receives net = gross − fee, the fee is deducted on the spot. The fee is a percent of the gross amount, charged once, by number of installments (credit, fees not passed on): 1x 3.09%, 2x 5.79%, 3x 6.09%, 4x 7.99%, 5x 8.09%, 6x 8.19%, 7x 9.49%, 8x 9.68%, 9x 10.37%, 10x 11.05%, 11x 12.27%, 12x 12.38% (debit 0.89%, not used for cash-out). R$ 1,000 at 1x → cost R$ 30.90, net R$ 969.10. Table lives in `CashAdvanceFeeTable`; update it when the terminal changes fees. The expense is the fee only. The gross card charge (e.g. the terminal's merchant line on the card statement) is `TransactionType.CardCashAdvance`: not spending. `mark-cashout "<description>"` marks it and remembers the description for future imports. The sheet already records the fee as Juros → Saque Cartão, so counting the gross would double the cost.
- **Ambiguity**: a description seen in history with different categories (e.g. generic "PAGTO ELETRON COBRANCA") is never guessed (`Classifier.Observe`/`IsAmbiguous`); it stays pending until the user decides (`Learn`/`classify`).
- **Manual rules**: `data/rules.csv` (pattern,category,item) → `apply-rules` saves "contains" rules and applies them to pending entries. Suggested by Claude, confirmed by the user.
- Normalizer folds Greek/Cyrillic lookalike letters to Latin (card exports sometimes spell merchants that way). Changing normalization changes dedup keys: delete `data/cashpilot.db` and re-import.
- **Classification learns**, in layers: exact normalized match → manual "contains" rule → trigram similarity ≥ 0.8 → unknown (ask the user, then `Learn`).
- **Imports are idempotent**: `Transaction.ComputeDedupKey(occurrence)`; same file twice must not duplicate. Installments (e.g. 03/06) are part of the key.
- Signed amounts: negative = outflow, positive = inflow.

## Source spreadsheet (the user's existing Google Sheets, exported)
Tabs: `Principal` (monthly pivot by category/item), `Gastos` (columns Dia, Categoria, Ítem, Conta, Valor, Obs; ~600 rows, values like "R$ 1.234,56", dates dd/MM/yyyy, includes future installments), `Cadastros1` (lists of categories, items and accounts), `Página3` (category → items matrix).
Known quirks: many rows have no category (marketplace/wallet purchases) → this is the initial training queue for the classifier; some installments repeat across months; "Cartão de Crédito" under Juros is card-bill interest, not the bill payment itself.
The real spreadsheet and statements contain personal data. This repo is **public**: never commit them (`/data/` is git-ignored), nor family names or account numbers. Use invented data in tests.

## Layout
- `src/CashPilot.Domain` — pure rules, no dependencies (Descriptions, Transactions, Interest, Reports)
- `src/CashPilot.Infrastructure` — `Persistence/CashPilotStore` (SQLite via Microsoft.Data.Sqlite, money as integer cents, schema versioned with `PRAGMA user_version`) and `Importing/GastosImporter` (CSV of the Gastos tab)
- `src/CashPilot.Web` — Blazor Web App (interactive server) + minimal JSON API (`/api/months`), same domain and SQLite (db found at repo root `data/cashpilot.db`). Run: `dotnet run --project src/CashPilot.Web` → http://localhost:5080. Pages: Resumo (monthly spending by category), Pendentes (classify, mark cash-out). Mobile later reuses the API (MAUI Blazor Hybrid can reuse the components).
- `src/CashPilot.Cli` — `import-gastos`, `apply-rules`, `mark-cashout`, `report`, `pending`, `classify`, `stats`; default DB `data/cashpilot.db` (git-ignored)
- `tests/CashPilot.Domain.Tests`, `tests/CashPilot.Infrastructure.Tests` — xUnit (`:memory:` SQLite)

## Report
`SpendingReport` (Domain/Reports): spending = Expense (and Undefined outflows); excluded = InternalTransfer, CardBillPayment, CardCashAdvance. A categorized inflow (refund) reduces its category; an uncategorized inflow is ignored. Uncategorized spending is shown separately, never dropped. The sheet's `Principal` tab only sums rows that have a category, so its monthly totals will not match this report; the gap is the uncategorized rows plus anything excluded above.

## Importer assumptions (confirm against the real export)
- Gastos columns: `Dia, Categoria, Ítem, Conta, Valor, Obs`. **Obs holds the merchant text**; Categoria/Ítem are the user's labels. If Obs is empty, Ítem (then Categoria) is the description.
- `Valor` is positive for spending (stored negative); a negative `Valor` is an inflow (refund). Option `ValuesArePositiveExpenses`.
- Rows with a category and a merchant text teach the classifier; rows without one are classified by it or stay pending.
- Installments only from explicit "PARC 03/06" text (a bare "01/09" is treated as a date).
- Comma or semicolon CSV is auto-detected.

## Roadmap
0. Domain + tests — done
1. SQLite persistence, `Gastos` CSV importer, manual rules, ambiguity, card cash-out type, monthly report — done (70 tests passing on the user's machine, .NET 10.0.12). Real sheet imported: 597 rows, ~86 still pending classification (the user classifies them with `classify`/`rules.csv`). Next in this phase: CSV/OFX bank-statement importers + transfer detection on stored data
1b. Web app (in progress): Resumo + Pendentes done → next: Lançamentos list with filters, Importar (upload + preview), Categorias/Regras CRUD → Contas (cadastro with limit/closing/due; replaces free-text account names) → Calendário/Simulador
2. Accounts, limits, card bills (closing/due dates), per-account interest rules
3. Payment calendar, cash-flow forecast, cost simulator (cash-out vs overdraft vs late boleto vs revolving credit)
4. C# API, then mobile (.NET MAUI or Flutter) on the same API; Postgres instead of SQLite

## Working notes
- The cloud sandbox that Claude runs in has no .NET SDK: the user runs `dotnet test` and reports results. Don't claim code was compiled unless it was.
- The sandbox cannot push to GitHub (no credentials): the user runs `git push` from Windows.
- Commits end with the Co-Authored-By / Claude-Session trailers.
