# FellowCore — Estrategia Comercial e Pricing

Data: 2026-05-03

## 1. Posicionamento

```
FellowCore = checkout + split + subcontas/sellers + ledger + payout + reconciliacao
```

Diferencial: plataforma completa de pagamentos para marketplaces e plataformas SaaS, nao apenas gateway de pagamento.

## 2. Planos Comerciais

| Plano | Perfil | Mensalidade |
|---|---|---:|
| **Comece** | Quem quer vender online sem capital de giro | R$0 |
| **Cresca** | Seller recorrente, volume medio | R$0 |
| **Scala** | Empresas em escala, alto volume, operacao assistida | R$499/mes |

## 3. Taxas Por Plano

### 3.1 Pix Recebido

| Plano | Taxa | Minimo | Maximo |
|---|---:|---:|---:|
| Comece | 1,19% | R$0,59 | Sem teto |
| Cresca | 0,99% | R$0,59 | R$7,90 |
| Scala | 0,89% | R$0,55 | R$5,90 |

### 3.2 Cartao De Credito A Vista

| Plano | Taxa |
|---|---:|
| Comece | 4,89% + R$0,49 |
| Cresca | 4,59% + R$0,39 |
| Scala | 4,49% + R$0,29 |

### 3.3 Cartao De Credito Parcelado

| Plano | Taxa |
|---|---:|
| Comece | 5,89% + R$0,49 |
| Cresca | 5,49% + R$0,39 |
| Scala | 5,19% + R$0,29 |

### 3.4 Cartao De Debito

| Plano | Taxa |
|---|---:|
| Comece | 4,89% + R$0,49 |
| Cresca | 4,59% + R$0,39 |
| Scala | 4,49% + R$0,29 |

### 3.5 Boleto Pago

| Plano | Taxa |
|---|---:|
| Comece | R$3,99 |
| Cresca | R$3,79 |
| Scala | R$3,49 |

### 3.6 Saque / Payout

| Plano | Taxa |
|---|---:|
| Comece | R$1,49 por saque |
| Cresca | R$1,19 por saque |
| Scala | 50 inclusos/mes, depois R$0,99 |

## 4. Custos De Provider

| Provider | Metodo | Custo |
|---|---|---:|
| OpenPix | Pix recebido | 0,80%, min R$0,50, max R$5,00 |
| OpenPix | Pix out / saque < R$500 | R$1,00 |
| OpenPix | Saque >= R$500 | R$0,00 |
| Stripe | Cartao nacional | 3,99% + R$0,39 |
| Stripe | Cartao internacional | +2,00% adicional |
| Stripe | Boleto pago | R$3,45 |
| Stripe | Pix | 1,19% (se habilitado) |
| Stripe | Dispute | R$55,00 por disputa |
| Stripe Connect | Platform pricing | +0,25% conforme modelo |

## 5. Margem Por Ticket (Plano Comece, Pix)

| Ticket | Taxa FellowCore | Custo OpenPix | Margem |
|---:|---:|---:|---:|
| R$10 | R$0,59 (min) | R$0,50 (min) | R$0,09 |
| R$100 | R$1,19 | R$0,80 | R$0,39 |
| R$500 | R$5,95 | R$4,00 | R$1,95 |
| R$1.000 | R$11,90 | R$5,00 (max) | R$6,90 |
| R$5.000 | R$59,50 | R$5,00 (max) | R$54,50 |

## 6. Margem Por Ticket (Plano Comece, Cartao A Vista)

| Ticket | Taxa FellowCore | Custo Stripe | Margem |
|---:|---:|---:|---:|
| R$10 | R$0,98 | R$0,79 | R$0,19 |
| R$100 | R$5,38 | R$4,38 | R$1,00 |
| R$500 | R$24,94 | R$20,34 | R$4,60 |
| R$1.000 | R$49,39 | R$40,29 | R$9,10 |
| R$5.000 | R$244,99 | R$199,89 | R$45,10 |

## 7. Pesquisa De Mercado

| Concorrente | Pix | Cartao 1x | Boleto | Saque |
|---|---:|---:|---:|---:|
| Abacate Pay | 0,89%-1,50% | N/A | N/A | R$1,00-2,00 |
| Mercado Pago | 0,99% | 4,98% | R$3,99 | Gratis |
| Pagar.me | 1,19% | 4,49%+R$0,49 | R$3,49 | R$3,67 |
| Asaas | 0,99% | 4,89% | R$4,99 | R$1,50-2,50 |
| iugu | 0,90%-1,50% | 4,49%+R$0,49 | R$3,49 | R$2,00 |
| Stripe | 1,19% | 3,99%+R$0,39 | R$3,45 | Variavel |
| OpenPix | 0,80% min R$0,50 | N/A | N/A | R$1,00 |

## 8. Criterios De Elegibilidade

### Cresca
- Volume >= R$5.000/mes OU >= 50 transacoes capturadas/mes

### Scala
- Volume >= R$100.000/mes OU contrato manual

## 9. Split Marketplace

| Plano | Limite De Recebedores Por Transacao |
|---|---:|
| Comece | Ate 2 |
| Cresca | Ate 10 |
| Scala | Ate 50 (ilimitado conforme contrato) |

## 10. Regra Principal

```
PlatformFeeAmount = quanto FellowCore cobra do seller (baseado no plano)
ProviderCostAmount = quanto Stripe/OpenPix cobra do FellowCore
PlatformMarginAmount = PlatformFeeAmount - ProviderCostAmount
SellerNetAmount = GrossAmount - PlatformFeeAmount
```

Todos os valores sao persistidos na transacao para report/reconciliacao.
