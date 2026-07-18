# FellowCore - Claude Code Action Plan para chegar a 100%

Data: 2026-05-04

Objetivo: orientar o Claude Code a fechar os gaps que impedem o FellowCore de ser considerado 100% pronto em build/release, observabilidade, lucro liquido, custos de provider e split marketplace avancado.

Regra geral: nao marcar nenhum item como concluido sem evidencia objetiva: codigo, teste automatizado, migracao, dashboard, alerta, runbook ou resultado de execucao.

---

## 0. Primeira obrigacao: estabilizar a branch

Antes de mexer em regra financeira, o Claude Code deve deixar o projeto compilando e testavel.

### 0.1 Corrigir restore/build

Problema atual:

- `dotnet test` falha no restore.
- `Kralizek.Extensions.Configuration.AWSSecretsManager >= 2.0.1` nao existe no NuGet publico.
- Ha conflito entre `AWSSDK.SecretsManager` 3.x e `AWSSDK.S3` 4.x via `AWSSDK.Core`.

Tarefas:

- Remover ou substituir `Kralizek.Extensions.Configuration.AWSSecretsManager` por pacote existente.
- Alinhar todos os pacotes AWS na mesma major version.
- Preferir AWS SDK v4 se o restante da infraestrutura ja usa `AWSSDK.S3` v4.
- Se Secrets Manager nao for essencial para local/test, isolar a integracao atras de feature flag ou adapter proprio.
- Rodar:

```bash
dotnet restore
dotnet build
dotnet test
```

Criterio de aceite:

- `dotnet restore` passa.
- `dotnet build` passa.
- `dotnet test` passa ou deixa apenas testes sandbox explicitamente skipped.
- CI deve conseguir executar os mesmos comandos.

### 0.2 Consolidar worktree

Problema atual:

- Ha muitos arquivos modificados, novos e deletados.
- Isso dificulta saber o que esta pronto para release.

Tarefas:

- Separar mudancas em commits logicos:
  - build/dependencies
  - observability
  - pricing/provider-cost
  - ledger-margin
  - marketplace-split
  - tests/docs
- Nao reverter mudancas de usuario sem permissao.
- Remover arquivos gerados desnecessarios versionados, se houver.

Criterio de aceite:

- `git status --short` deve mostrar apenas mudancas intencionais antes do merge final.

---

## 1. Observabilidade Grafana/Prometheus em 100%

Estado atual:

- A API expoe `/metrics`.
- Prometheus tem config em `infra/prometheus/prometheus.yml`.
- Grafana tem datasource e dashboards provisionados.
- Alertas existem em `infra/alerts/fellowcore-alerts.yml`.
- Porem, Prometheus/Grafana nao estao no `docker-compose.yml`.
- Muitos alertas usam metricas que a aplicacao ainda nao emite.

### 1.1 Subir Prometheus e Grafana no Docker Compose

Tarefas:

- Adicionar servico `prometheus` ao `docker-compose.yml`.
- Montar:
  - `infra/prometheus/prometheus.yml` em `/etc/prometheus/prometheus.yml`
  - `infra/alerts/fellowcore-alerts.yml` em `/etc/prometheus/alerts/fellowcore-alerts.yml`
- Adicionar servico `grafana`.
- Montar:
  - `infra/grafana/provisioning/datasources`
  - `infra/grafana/provisioning/dashboards`
  - `infra/grafana/dashboards`
- Persistir volume de dados do Grafana.
- Expor portas locais:
  - Prometheus: `9090`
  - Grafana: `3000`

Criterio de aceite:

- `docker compose up` sobe api, postgres, redis, minio, prometheus e grafana.
- `http://localhost:9090/targets` mostra `fellowcore-api` UP.
- Grafana abre com datasource Prometheus provisionado.
- Dashboards FellowCore aparecem automaticamente.

### 1.2 Implementar todas as metricas usadas pelos alertas

Metricas atualmente esperadas nos alertas:

- `fellowcore_circuit_breaker_state`
- `fellowcore_provider_errors_total`
- `fellowcore_provider_requests_total`
- `fellowcore_webhook_delivery_failures_total`
- `fellowcore_outbox_dead_letter_count`
- `fellowcore_reconciliation_issues_total`
- `fellowcore_ledger_global_imbalance_cents`
- `fellowcore_payouts_pending_age_hours`
- `fellowcore_payouts_failed_total`
- `fellowcore_disputes_open_count`
- `fellowcore_hangfire_last_heartbeat_seconds`
- `fellowcore_hangfire_failed_count`
- `fellowcore_health_check_status`

Tarefas:

- Expandir `FellowCoreMetrics`.
- Para counters de eventos, registrar no ponto onde o evento acontece.
- Para gauges de estado, usar observable gauges com leitura de repositorios/servicos.
- Garantir labels estaveis e de baixa cardinalidade.
- Nao usar `transaction_id`, `seller_id`, `tenant_id` em metricas Prometheus de alta frequencia.

Criterio de aceite:

- Cada metrica acima aparece em `/metrics`.
- Cada alerta do arquivo `fellowcore-alerts.yml` tem metrica correspondente emitida pela aplicacao.
- Criar testes unitarios ou de integracao leve para validar que os instrumentos sao registrados.

### 1.3 Corrigir alertas que usam metricas incorretas

Tarefas:

- Validar nomes reais das metricas do OpenTelemetry ASP.NET Core.
- Corrigir `http_server_request_duration_seconds_*` se o nome exportado estiver diferente.
- Corrigir alerta de CPU: `process_cpu_seconds_total > 0.80` nao mede percentual de CPU diretamente.
- Trocar para expressao baseada em `rate(process_cpu_seconds_total[5m])`.
- Adicionar `for` e thresholds coerentes.

Criterio de aceite:

- `promtool check rules infra/alerts/fellowcore-alerts.yml` passa.
- Alertas aparecem em Prometheus.
- Pelo menos um alerta de teste pode ser disparado em ambiente local/staging.

### 1.4 Configurar canal real de alerta

Tarefas:

- Escolher canal: Slack, Discord, PagerDuty, OpsGenie ou email operacional.
- Versionar config de Alertmanager ou Grafana Alerting, sem secrets no repo.
- Definir severidades:
  - P0: dinheiro, ledger, DB indisponivel
  - P1: pagamentos, webhooks, payouts
  - P2: degradacao operacional
- Documentar responsavel/on-call.

Criterio de aceite:

- Alerta de teste chega no canal real.
- Runbook aponta o canal e responsavel.

---

## 2. Lucro liquido e custo de provider em 100%

Estado atual:

- A transacao tem:
  - `PlatformFeeAmount`
  - `ProviderCostAmount`
  - `PlatformMarginAmount`
- `PlatformMarginAmount = PlatformFeeAmount - ProviderCostAmount`.
- `ProviderCostSchedule` calcula custo estimado por provider/metodo.
- Porem, o ledger ainda nao separa de forma completa receita da plataforma, custo do provider e lucro liquido.

### 2.1 Transformar margem em lancamento contabil real

Tarefas:

- Criar contas de ledger de plataforma, se ainda nao existirem:
  - `PLATFORM_FEE`
  - `PROVIDER_COST`
  - `PLATFORM_MARGIN`
  - manter `PLATFORM_RECEIVABLE` e `PLATFORM_PAYOUT`
- Ao capturar uma transacao, registrar:
  - entrada bruta ou liquida conforme rail
  - credito do seller pelo valor liquido devido
  - receita bruta da plataforma em `PLATFORM_FEE`
  - custo do provider em `PROVIDER_COST`
  - margem liquida em `PLATFORM_MARGIN`
- A margem deve ser resultado contabil, nao apenas campo calculado.

Exemplo esperado:

```text
Venda: 100.00
Taxa FellowCore cobrada do seller: 1.50
Custo provider: 0.80
Margem FellowCore: 0.70
Seller net: 98.50

Ledger deve conseguir demonstrar:
- Seller recebeu 98.50
- Plataforma reconheceu receita bruta 1.50
- Plataforma reconheceu custo provider 0.80
- Plataforma ficou com margem 0.70
```

Criterio de aceite:

- Ledger por transacao demonstra taxa, custo e margem.
- Dashboard financeiro consegue somar:
  - gross volume
  - seller net
  - platform fees
  - provider costs
  - gross margin
- Testes cobrem PIX, cartao credito, debito e boleto.

### 2.2 Separar custo estimado de custo real reconciliado

Problema:

- `ProviderCostSchedule` calcula custo esperado.
- O custo real pode divergir do settlement report Stripe/OpenPix.

Tarefas:

- Manter `ProviderCostAmount` como custo estimado no momento da transacao.
- Adicionar campo ou entidade para custo real reconciliado:
  - `ProviderCostActualAmount`
  - ou lancamento de ajuste via settlement reconciliation.
- Quando settlement externo chegar, reconciliar:
  - custo estimado vs custo real
  - margem estimada vs margem real
- Criar issue de reconciliacao se divergencia passar tolerancia.

Criterio de aceite:

- Relatorio financeiro mostra margem estimada e margem real.
- Divergencias de custo provider viram `ReconciliationIssue`.

### 2.3 Corrigir fallback silencioso de custo provider zero

Problema:

- `ProviderCostService` retorna `0` quando nao encontra schedule.
- Isso pode inflar lucro artificialmente em producao.

Tarefas:

- Em Production, falhar ou gerar alerta quando nao houver `ProviderCostSchedule` ativo para provider/metodo.
- Em Development/Testing, permitir fallback controlado.
- Logar com severidade adequada.

Criterio de aceite:

- Transacao em Production nao calcula margem falsa com custo zero sem alerta.
- Teste cobre ausencia de schedule em Production.

---

## 3. Split marketplace avancado em 100%

Estado atual:

- Existe split por valor fixo ou percentual.
- Existe `SplitRule`.
- Existe `SplitTransfer`, mas ele nao parece integrado ao fluxo real.
- `SplitProcessor` cria payouts para destinatarios.
- O ledger credita o seller principal com o liquido da transacao.
- Falta um modelo robusto de split financeiro por recebedor.

Objetivo: transformar split em split marketplace real, com ledger por recebedor, reserva, reversao, refund/chargeback proporcional e politica de alocacao de fees.

### 3.1 Nao fazer payout direto sem saldo reservado

Problema:

- O split atual chama `PayoutService.CreateAsync` para cada recebedor.
- Marketplace maduro deve primeiro creditar/reservar saldo do recebedor no ledger.

Tarefas:

- Alterar fluxo:
  1. Transacao capturada.
  2. Calcular distribuicao de split sobre o valor liquido.
  3. Criar `SplitTransfer` para cada recebedor.
  4. Debitar origem apropriada.
  5. Creditar ledger de cada recebedor.
  6. Marcar `SplitTransfer` como `RESERVED` ou `PAID_TO_BALANCE`.
  7. Payout externo deve ser etapa separada, opcional, baseada no saldo do recebedor.

Criterio de aceite:

- Cada recebedor tem saldo no ledger antes de qualquer payout.
- Payout falho nao invalida o split contabil ja reservado.
- `SplitTransfer` passa a ser a fonte de verdade do split.

### 3.2 Implementar `FeeAllocationPolicy`

Estado atual:

- `FeeAllocationPolicy` existe no DTO/enum, mas nao e aplicado no fluxo.

Politicas existentes:

- `PRIMARY_SELLER_PAYS_FEES`
- `PROPORTIONAL_TO_RECIPIENTS`
- `PLATFORM_ABSORBS`

Tarefas:

- Implementar calculo real para cada politica:

#### PRIMARY_SELLER_PAYS_FEES

- Taxas saem do seller principal.
- Recebedores recebem valores/percentuais definidos sobre base acordada.

#### PROPORTIONAL_TO_RECIPIENTS

- Taxas sao rateadas entre recebedores proporcionalmente ao valor de cada split.
- Cada recebedor recebe seu bruto menos sua parte da taxa.

#### PLATFORM_ABSORBS

- Recebedores recebem o valor bruto/contratado.
- Plataforma absorve provider cost e/ou fee comercial.
- Ledger deve refletir impacto negativo na margem.

Criterio de aceite:

- Testes unitarios para as tres politicas.
- Testes de integracao confirmam ledger final por recebedor.
- Nenhum split pode gerar saldo negativo sem rejeicao explicita.

### 3.3 Resolver mistura de split fixo e percentual com prioridade

Estado atual:

- `SplitRuleRecipient` tem `Priority`.
- Percentual e fixo podem coexistir.

Tarefas:

- Definir regra formal:
  - valores fixos sao alocados primeiro por prioridade
  - percentuais sao aplicados sobre valor restante ou sobre net original?
- Documentar a decisao.
- Implementar a decisao em um `SplitCalculationService`.
- Rejeitar configuracoes impossiveis.

Criterio de aceite:

- Testes cobrem:
  - apenas percentual
  - apenas fixo
  - fixo + percentual
  - fixos maiores que valor disponivel
  - arredondamento
  - sobra de centavos

### 3.4 Criar servico dedicado de calculo de split

Tarefas:

- Criar `ISplitCalculationService`.
- Entrada:
  - amount bruto
  - net amount
  - platform fee
  - provider cost
  - recipients
  - fee allocation policy
- Saida:
  - lista de recebedores
  - gross share
  - fee share
  - provider cost share
  - net share
  - rounding adjustment
- Nao deixar regra financeira espalhada no `TransactionService`.

Criterio de aceite:

- `TransactionService` apenas orquestra.
- Toda regra matematica de split fica em servico testado.

### 3.5 Refund e chargeback proporcional por recebedor

Problema:

- Marketplace real precisa reverter split quando ha refund/chargeback.

Tarefas:

- Em refund parcial:
  - calcular reversao proporcional por recebedor
  - debitar saldo do recebedor ou criar saldo negativo controlado/receivable
  - ajustar `SplitTransfer`
- Em refund total:
  - reverter todos os splits da transacao
- Em chargeback:
  - congelar ou debitar recebedores proporcionalmente
  - aplicar regra para quem assume fee de disputa

Criterio de aceite:

- Testes de refund parcial com 2+ recebedores.
- Testes de refund total.
- Testes de chargeback ganho/perdido.
- Ledger permanece balanceado.

### 3.6 Idempotencia e concorrencia do split

Tarefas:

- Garantir chave unica por `(TenantId, TransactionId, RecipientSellerId)` ou idempotency key por split.
- Evitar processamento duplo por Hangfire.
- Usar status transitions de `SplitTransfer`.
- Usar optimistic concurrency onde necessario.

Criterio de aceite:

- Rodar teste de concorrencia com dois workers processando a mesma transacao.
- Resultado: nenhum recebedor e creditado duas vezes.

### 3.7 Integrar split com reconciliacao

Tarefas:

- Reconciliation deve validar:
  - soma dos splits <= net da transacao
  - soma creditada aos recebedores bate com `SplitTransfer`
  - payout externo nao excede saldo do recebedor
  - split revertido em refund/chargeback
- Criar `ReconciliationIssue` especifico para split:
  - `SPLIT_TOTAL_MISMATCH`
  - `SPLIT_LEDGER_MISSING`
  - `SPLIT_DUPLICATE_CREDIT`
  - `SPLIT_REFUND_NOT_REVERSED`

Criterio de aceite:

- Reconciliation detecta divergencias de split.
- Dashboard financeiro mostra split issues criticas.

---

## 4. Dashboards financeiros em 100%

Tarefas:

- Atualizar dashboard financeiro para mostrar:
  - TPV / GMV
  - receita bruta FellowCore
  - custo provider
  - margem bruta
  - margem real reconciliada
  - volume por provider
  - volume por metodo de pagamento
  - splits pendentes
  - splits falhos
  - payouts pendentes
  - ledger imbalance
  - reconciliation issues

Criterio de aceite:

- Dashboard responde com dados reais de metricas emitidas.
- Nenhum painel usa metrica inexistente.

---

## 5. Testes obrigatorios para considerar 100%

### 5.1 Pricing/provider cost

Criar ou completar testes para:

- PIX OpenPix com custo provider.
- Cartao Stripe com custo provider.
- Boleto Stripe com custo provider.
- Provider cost ausente em Production.
- Margem negativa quando plataforma absorve custo.
- Arredondamento em centavos.

### 5.2 Ledger margin

Criar testes para:

- Captura gera contas de plataforma corretas.
- `PLATFORM_FEE`, `PROVIDER_COST` e `PLATFORM_MARGIN` fecham.
- Refund reverte proporcionalmente taxa e margem.
- Settlement real ajusta custo estimado.

### 5.3 Split marketplace

Criar testes para:

- 2 recebedores percentuais.
- 2 recebedores fixos.
- Fixo + percentual com prioridade.
- `PRIMARY_SELLER_PAYS_FEES`.
- `PROPORTIONAL_TO_RECIPIENTS`.
- `PLATFORM_ABSORBS`.
- Refund parcial.
- Refund total.
- Chargeback.
- Worker concorrente.
- Reconciliation detectando divergencia.

### 5.4 Observability

Criar testes/verificacoes para:

- `/metrics` expoe metricas customizadas.
- Prometheus sobe com rules validas.
- Grafana provisiona dashboards.
- Alertas usam metricas existentes.

---

## 6. Comandos finais de validacao

Claude Code deve executar e registrar resultado:

```bash
dotnet restore
dotnet build
dotnet test
docker compose config
docker compose up -d
curl -i --max-time 8 http://localhost:5195/health
curl -i --max-time 8 http://localhost:5195/metrics
```

Se Prometheus/Grafana estiverem no compose:

```bash
curl -i --max-time 8 http://localhost:9090/-/ready
curl -i --max-time 8 http://localhost:3000/api/health
```

Validar regras Prometheus:

```bash
promtool check rules infra/alerts/fellowcore-alerts.yml
```

---

## 7. Definicao de pronto

O FellowCore so deve ser marcado como 100% nestes pontos quando todos forem verdadeiros:

- Build e testes passam.
- CI passa.
- Grafana e Prometheus sobem local/staging.
- Alertas usam metricas reais.
- Canal real de alerta foi testado.
- Provider cost nao pode virar zero silenciosamente em Production.
- Ledger separa taxa da plataforma, custo provider e margem.
- Split credita recebedores no ledger antes de payout externo.
- `FeeAllocationPolicy` funciona de verdade.
- Refund/chargeback revertem split proporcionalmente.
- Reconciliation cobre margem, provider cost e split.
- Dashboards mostram dados reais, nao metricas inexistentes.

