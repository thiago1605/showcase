# Fellow Pay - Portal Financeiro

Portal operacional da Fellow Pay, parte do ecossistema Grupo Fellow. Frontend construído com Next.js 16, React 19, Tailwind CSS 4 e TypeScript.

## Stack

- **Framework:** Next.js 16 (App Router)
- **UI:** Tailwind CSS 4, componentes customizados
- **Tipagem:** TypeScript 5
- **Charts:** ApexCharts (react-apexcharts)
- **Base:** Template TailAdmin adaptado

## Como rodar

```bash
# Instalar dependências
npm install

# Desenvolvimento
npm run dev

# Build de produção
npm run build

# Rodar build
npm start

# Lint
npm run lint
```

O portal estará disponível em `http://localhost:3000`.

## Configuração da API

Copie o arquivo de exemplo e configure:

```bash
cp .env.local.example .env.local
```

Variáveis disponíveis:

| Variável | Descrição | Default |
|----------|-----------|---------|
| `NEXT_PUBLIC_API_BASE_URL` | URL base da Fellow Core API | `http://localhost:5000` |

## Estrutura do Projeto

```
src/
  app/
    (admin)/              # Layout com sidebar (área autenticada)
      page.tsx            # Dashboard financeiro
      transactions/       # Gestão de transações
      sellers/            # Gestão de sellers
      customers/          # Gestão de clientes
      payment-links/      # Payment links
      split-rules/        # Regras de split
      split-simulator/    # Simulador de split
      payouts/            # Payouts/saques
      refunds/            # Reembolsos
      disputes/           # Disputas/chargebacks
      subscriptions/      # Assinaturas recorrentes
      receipts/           # Recibos
      webhooks/           # Endpoints de webhook
      reconciliation/     # Reconciliação financeira
      reports/            # Relatórios automáticos
      users/              # Gestão de usuários
      settings/           # Configurações do tenant
    (full-width-pages)/   # Layout sem sidebar (auth, errors)
  components/
    dashboard/            # Componentes do dashboard
    common/               # Componentes reutilizáveis
    ui/                   # Componentes base (buttons, modals, etc.)
  context/                # React Context (Theme, Sidebar)
  layout/                 # AppSidebar, AppHeader, Backdrop
  lib/api/                # Cliente HTTP para Fellow Core
  services/               # Services por domínio
  types/                  # TypeScript interfaces/types
```

## Autenticação

O portal usa JWT Bearer tokens via Fellow Core `/api/v1/auth/login`.
API Keys (X-Api-Key) nunca são expostas no frontend.

Fluxo planejado:
1. Login com email/senha
2. MFA/TOTP se habilitado
3. Token armazenado no client
4. Refresh automático

## Nomenclatura

- **Fellow Pay** = Este portal/produto (frontend visível ao usuário)
- **Fellow Core** = API/backend (referenciado apenas em contexto técnico)
- **Grupo Fellow** = Ecossistema completo

## Pendências para próxima etapa

- [ ] Implementar autenticação JWT completa (login, MFA, refresh)
- [ ] Integrar dashboard com Fellow Core `/api/v1/dashboard`
- [ ] Conectar listagens reais (transações, sellers, etc.)
- [ ] Implementar filtros e paginação nas tabelas
- [ ] Adicionar exportação (CSV/PDF)
- [ ] Implementar detalhes de transação
- [ ] Configurar WebSocket/SignalR para updates em tempo real
- [ ] Testes E2E
