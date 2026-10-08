# CashPilot

Personal cash-flow control for people juggling several bank accounts, credit cards, boletos and receivables — built around one question: **what is the cheapest way to cover my payments this month?**

> Português: veja [README.pt-BR.md](README.pt-BR.md).

## Goals

- Import statements (CSV/OFX first, then PDF/images), **idempotently** — re-sending a file never duplicates entries.
- A classifier that **learns**: it categorizes by description, asks only when unsure, and remembers the answer.
- Pix/TED between your own accounts and credit card bill payments are **not** counted as spending.
- All interest rolled up as "Juros e encargos" with a type: overdraft, late boleto, late card payment, card cash-out fee, bank fees.
- Payment calendar, account/card limits, and an interest simulator that compares ways to raise cash.

## Structure

| Project | Contents |
|---|---|
| `src/CashPilot.Domain` | Pure rules, no dependencies: description normalizer, learning classifier, transfer detector, card cash-out calculator |
| `src/CashPilot.Infrastructure` | SQLite store (`Microsoft.Data.Sqlite`) and the importer for the spreadsheet's "Gastos" tab (CSV) |
| `src/CashPilot.Cli` | Command line: `import-gastos`, `pending`, `classify`, `stats` |
| `tests/CashPilot.Domain.Tests`, `tests/CashPilot.Infrastructure.Tests` | xUnit tests |

## Run

```
dotnet test
```

Import your spreadsheet (export the "Gastos" tab as CSV into `data/`, which is git-ignored):

```
dotnet run --project src/CashPilot.Cli -- import-gastos data/gastos.csv
dotnet run --project src/CashPilot.Cli -- apply-rules data/rules.csv   # pattern,category,item
dotnet run --project src/CashPilot.Cli -- pending
dotnet run --project src/CashPilot.Cli -- classify "Some Store" "Compras" "Geral"
```

The database is created at `data/cashpilot.db` (override with `--db`). Importing the same file again inserts nothing.

## Card cash-out example

The card is charged the gross amount, the account receives the net, and the difference is the expense.
R$ 1,000 at 3.09% (1x): cost R$ 30.90, net R$ 969.10. The fee table by number of installments (`CashAdvanceFeeTable`) ships with the terminal's credit fees for 1x to 12x (3.09% to 12.38%).

## Roadmap

0. Domain + tests (done)
1. Persistence (SQLite) and CSV/OFX importer; seed from the current spreadsheet
2. Accounts, limits, bills and per-account interest rules
   - "Bills to pay" screen: everything due, shown next to the bank accounts and balances (where to take the money from)
   - Overdraft (LIS) settings per account: interest-free days and interest rate
   - Statement import with preview (Bradesco CSV/PDF, Itaú PDF; OFX, other banks and images next), transfer pairing between own accounts, soft delete
3. Payment calendar, cash-flow forecast and cost simulator
4. C# API and mobile app

> Category names and bank-statement patterns are in Portuguese on purpose: they are data from Brazilian banks.
