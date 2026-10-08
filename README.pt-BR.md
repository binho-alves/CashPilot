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

## Saque na maquininha

O cartão é cobrado pelo valor bruto, a conta recebe o líquido, e a diferença é a despesa.
R$ 1.000 a 3,08% (1x): custo R$ 30,80, líquido R$ 969,20.
