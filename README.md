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
| `tests/CashPilot.Domain.Tests` | xUnit tests |

## Run

```
dotnet test
```

## Card cash-out example

The card is charged the gross amount, the account receives the net, and the difference is the expense.
R$ 1,000 at 3.08% (1x): cost R$ 30.80, net R$ 969.20. The fee table by number of installments (`CashAdvanceFeeTable`) ships with 1x only.

## Roadmap

0. Domain + tests (done)
1. Persistence (SQLite) and CSV/OFX importer; seed from the current spreadsheet
2. Accounts, limits, bills and per-account interest rules
3. Payment calendar, cash-flow forecast and cost simulator
4. C# API and mobile app

> Category names and bank-statement patterns are in Portuguese on purpose: they are data from Brazilian banks.
