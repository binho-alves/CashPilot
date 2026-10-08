# CashPilot

Controle de fluxo de caixa pessoal para quem tem várias contas, cartões, boletos e recebíveis. A pergunta central: **qual é a forma mais barata de cobrir meus pagamentos neste mês?**

## Objetivos

- Importar extratos (CSV/OFX primeiro; depois PDF e imagem) sem duplicar lançamentos.
- Classificador que **aprende** com as suas correções.
- Pix entre contas próprias e pagamento de fatura **não** contam como gasto.
- Juros agrupados em "Juros e encargos" por tipo: cheque especial, atraso de boleto, atraso de cartão, saque na maquininha e tarifas.
- Calendário de pagamentos, limites de contas e cartões e simulador de juros.

## Rodar

```
dotnet test
```

## Importar a planilha

Exporte a aba "Gastos" como CSV para a pasta `data/` (ignorada pelo git):

```
dotnet run --project src/CashPilot.Cli -- import-gastos data/gastos.csv
dotnet run --project src/CashPilot.Cli -- apply-rules data/rules.csv   # pattern,category,item
dotnet run --project src/CashPilot.Cli -- pending
dotnet run --project src/CashPilot.Cli -- classify "Nome da Loja" "Compras" "Geral"
```

O banco fica em `data/cashpilot.db` (mude com `--db`). Importar o mesmo arquivo de novo não duplica nada.

## Saque na maquininha

O cartão é cobrado pelo valor bruto, a conta recebe o líquido, e a diferença é a despesa.
R$ 1.000 a 3,09% (1x): custo R$ 30,90, líquido R$ 969,10. A tabela de taxas por parcela (`CashAdvanceFeeTable`) já vem com as taxas de crédito da maquininha, de 1x a 12x (3,09% a 12,38%).

## Próximos passos

- Importação pela web (envio do CSV) e cadastro de categorias e regras
- Cadastro de contas (limite, fechamento, vencimento)
- Tela "Contas a pagar" (feita): faturas dos cartões (pelo fechamento e vencimento), boletos avulsos e lançamentos futuros, ao lado das contas bancárias e saldos (saldo informado por você + lançamentos depois dele)
- Cheque especial (LIS) por conta: dias sem juros e taxa de juros
- Calendário de vencimentos e simulador de custo (feitos: LIS e saque na maquininha; faltam boleto atrasado e rotativo)
