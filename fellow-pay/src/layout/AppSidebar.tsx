"use client";
import React, { useEffect, useLayoutEffect, useRef, useState, useCallback } from "react";
import Link from "next/link";
import Image from "next/image";
import { usePathname } from "next/navigation";
import { useSidebar } from "../context/SidebarContext";
import { ChevronDownIcon, HorizontaLDots } from "../icons/index";

type NavItem = {
  name: string;
  icon: React.ReactNode;
  path?: string;
  subItems?: { name: string; path: string; icon?: React.ReactNode }[];
};

// --- SVG Icons ---
// Cada ícone abaixo é a versão sidebar do decorIcon do hero da page
// correspondente. Mesmas paths/conceitos visuais — sidebar e hero falam o
// mesmo idioma sobre cada destino.
const DashboardIcon = () => (
  // Bento 4-quadrantes assimétricos — mesmo do decorIcon do hero /Painel.
  <svg width="20" height="20" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
    <rect x="3" y="3" width="7" height="9" rx="1.5" />
    <rect x="14" y="3" width="7" height="5" rx="1.5" />
    <rect x="14" y="12" width="7" height="9" rx="1.5" />
    <rect x="3" y="16" width="7" height="5" rx="1.5" />
  </svg>
);

const TierIcon = () => (
  <svg width="20" height="20" viewBox="0 0 20 20" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M10 1.667l2.45 4.96 5.476.797-3.963 3.864.936 5.459L10 14.167l-4.9 2.58.936-5.459L2.073 7.424l5.477-.797L10 1.667Z" stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round"/>
  </svg>
);

const InsightsIcon = () => (
  // 4 barras crescentes — match com decorIcon do hero /insights.
  <svg width="20" height="20" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
    <line x1="4" y1="20" x2="4" y2="14" />
    <line x1="10" y1="20" x2="10" y2="10" />
    <line x1="16" y1="20" x2="16" y2="6" />
    <line x1="22" y1="20" x2="22" y2="3" />
  </svg>
);

const TransactionsIcon = () => (
  // Setas bidirecionais — match com decorIcon do hero /transactions.
  // Antes era 3 linhas com bullets (confundia com filtro/list).
  <svg width="20" height="20" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
    <path d="M16 3l4 4-4 4M20 7H4M8 21l-4-4 4-4M4 17h16" />
  </svg>
);

const CustomersIcon = () => (
  <svg width="20" height="20" viewBox="0 0 20 20" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M13.333 17.5v-1.667a3.333 3.333 0 0 0-3.333-3.333H5a3.333 3.333 0 0 0-3.333 3.333V17.5" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/>
    <circle cx="7.5" cy="5.833" r="3.333" stroke="currentColor" strokeWidth="1.5"/>
    <path d="M18.333 17.5v-1.667a3.333 3.333 0 0 0-2.5-3.225M13.333 2.608a3.333 3.333 0 0 1 0 6.459" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/>
  </svg>
);

const PaymentLinksIcon = () => (
  // Chain link — match com decorIcon do hero /payment-links.
  <svg width="20" height="20" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
    <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71" />
    <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71" />
  </svg>
);

const SplitIcon = () => (
  // Branching — match com decorIcon do hero /split-rules.
  <svg width="20" height="20" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
    <line x1="6" y1="3" x2="6" y2="15" />
    <circle cx="18" cy="6" r="3" />
    <circle cx="6" cy="18" r="3" />
    <path d="M18 9a9 9 0 0 1-9 9" />
  </svg>
);

const SimulatorIcon = () => (
  // Calculadora — match com decorIcon do hero /split-simulator.
  <svg width="20" height="20" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
    <rect x="4" y="2" width="16" height="20" rx="2" />
    <line x1="8" y1="6" x2="16" y2="6" />
    <line x1="8" y1="10" x2="9" y2="10" />
    <line x1="12" y1="10" x2="13" y2="10" />
    <line x1="16" y1="10" x2="16" y2="10" />
    <line x1="8" y1="14" x2="9" y2="14" />
    <line x1="12" y1="14" x2="13" y2="14" />
    <line x1="16" y1="14" x2="16" y2="18" />
    <line x1="8" y1="18" x2="13" y2="18" />
  </svg>
);

const PayoutsIcon = () => (
  // Carteira com seta saindo — bate com o decorIcon do hero da /payouts.
  // Antes era uma maleta/briefcase que não comunicava "saque/withdraw".
  <svg width="20" height="20" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
    <rect x="2" y="6" width="20" height="14" rx="2" />
    <path d="M16 12h4" />
    <path d="M18 10l2 2-2 2" />
  </svg>
);

const RefundsIcon = () => (
  // Seta curva voltando — match com decorIcon do hero /refunds.
  <svg width="20" height="20" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
    <path d="M3 7v6h6" />
    <path d="M21 17a9 9 0 0 0-15-6.7L3 13" />
  </svg>
);

const DisputesIcon = () => (
  <svg width="20" height="20" viewBox="0 0 20 20" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M10 6.667V10M10 13.333h.008" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/>
    <path d="M8.575 3.217 2.517 13.75a1.667 1.667 0 0 0 1.425 2.5h12.116a1.667 1.667 0 0 0 1.425-2.5L11.425 3.217a1.667 1.667 0 0 0-2.85 0Z" stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round"/>
  </svg>
);

const SubscriptionsIcon = () => (
  // Ciclo/repeat — bate com o decorIcon do hero da /subscriptions.
  // Antes era um cubo/package que confundia (parecia /products).
  <svg width="20" height="20" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
    <polyline points="23 4 23 10 17 10" />
    <polyline points="1 20 1 14 7 14" />
    <path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15" />
  </svg>
);

const ReceiptsIcon = () => (
  // Documento com dobra no canto — match com decorIcon do hero /receipts.
  <svg width="20" height="20" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
    <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
    <polyline points="14 2 14 8 20 8" />
    <line x1="8" y1="13" x2="16" y2="13" />
    <line x1="8" y1="17" x2="13" y2="17" />
  </svg>
);

const WebhooksIcon = () => (
  // Relógio/event — match com decorIcon do hero /webhooks.
  // Antes eram 3 nós conectados (parecia network/grafo).
  <svg width="20" height="20" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
    <circle cx="12" cy="12" r="9" />
    <path d="M12 8v4l3 3" />
    <polyline points="20 8 17 8 17 5" />
  </svg>
);

const IntegrationsIcon = () => (
  // Plug/connector — match com decorIcon do hero /integrations.
  // Producer-scoped webhooks são "integrações com ferramentas externas"
  // (RD Station, ActiveCampaign), por isso ícone de plug e não de relógio.
  <svg width="20" height="20" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
    <path d="M7 17a5 5 0 0 1-2-9.5A5.5 5.5 0 0 1 16 6a4 4 0 0 1 3.5 6" />
    <path d="M12 12v9" />
    <path d="m8 17 4 4 4-4" />
  </svg>
);

const ReportsIcon = () => (
  <svg width="20" height="20" viewBox="0 0 20 20" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M5.833 14.167V10M10 14.167V7.5M14.167 14.167V5" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/>
    <rect x="2.5" y="2.5" width="15" height="15" rx="2" stroke="currentColor" strokeWidth="1.5"/>
  </svg>
);

const TeamIcon = () => (
  // 2 pessoas + extra — match com decorIcon do hero /team (mesma família
  // do AffiliationsIcon mas com posicionamento diferente das figuras).
  <svg width="20" height="20" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
    <circle cx="9" cy="7" r="3" />
    <path d="M3 21v-2a4 4 0 0 1 4-4h4a4 4 0 0 1 4 4v2" />
    <circle cx="17" cy="7" r="3" />
    <path d="M21 21v-2a4 4 0 0 0-3-3.87" />
  </svg>
);

const ProductsIcon = () => (
  // Caixa 3D / package — match com decorIcon do hero /products.
  <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z" />
    <polyline points="3.27 6.96 12 12.01 20.73 6.96" />
    <line x1="12" y1="22.08" x2="12" y2="12" />
  </svg>
);
const MarketplaceIcon = () => (
  // Storefront — match com decorIcon do hero /affiliate-marketplace.
  <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <path d="M3 9l1-5h16l1 5" />
    <path d="M5 9v11a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V9" />
    <line x1="9" y1="14" x2="15" y2="14" />
  </svg>
);
const AffiliationsIcon = () => (
  // Users/network — match com decorIcon do hero /affiliations.
  <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <circle cx="9" cy="7" r="4" />
    <path d="M3 21v-2a4 4 0 0 1 4-4h4a4 4 0 0 1 4 4v2" />
    <circle cx="17" cy="7" r="3" />
    <path d="M21 21v-2a4 4 0 0 0-3-3.87" />
  </svg>
);
const CouponsIcon = () => (
  // Ticket com % — match com decorIcon do hero /coupons.
  <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <path d="M2 9a3 3 0 0 1 0 6v2a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-2a3 3 0 0 1 0-6V7a2 2 0 0 0-2-2H4a2 2 0 0 0-2 2z" />
    <line x1="9" y1="14" x2="15" y2="8" />
    <circle cx="9.5" cy="9.5" r="0.5" fill="currentColor" />
    <circle cx="14.5" cy="13.5" r="0.5" fill="currentColor" />
  </svg>
);
const SettingsIcon = () => (
  // Engrenagem — match com decorIcon do hero /settings.
  <svg width="20" height="20" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
    <circle cx="12" cy="12" r="3" />
    <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09a1.65 1.65 0 0 0-1-1.51 1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83 0 2 2 0 0 1 0-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 0-2.83 2 2 0 0 1 2.83 0l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 0 2 2 0 0 1 0 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
  </svg>
);

// Sidebar entries for the seller portal. Items hidden here are *not removed* from the
// codebase — they remain accessible by URL — but they're either tenant-wide resources
// not yet seller-safe, or features deliberately out of MVP scope. See auth_model.md
// for the full HOLD list.
//
// Hidden today:
//   /customers     — Customer entity has no SellerId yet (Codex HOLD)
//   /split-rules   — list/create require an ownership model on SplitRule (Codex HOLD)
//   /disputes      — no backend controller yet (TODO)
//   /reports       — ScheduledReport is tenant-wide, not seller-scoped (out of MVP)
// Webhooks volta como item visível (2026-05-08): a página é completa
// (criar/listar/toggle/remover) e o controller agora aceita [AuthOrApiKeyAuth].
// Continua tenant-wide — todos os usuários do mesmo tenant veem os mesmos
// endpoints. Para escopo por seller, P2 follow-up.
type NavSection = {
  /** ID estável usado como menuType na máquina de estado do submenu aberto. */
  id: string;
  /** Label uppercase do header da seção. */
  label: string;
  items: NavItem[];
};

// Organização em 5 seções ergonômicas (vs as 2 antigas VENDAS/CONTA infladas):
//
// INÍCIO       — análise (entrada do app)
// OPERAÇÃO     — fluxo financeiro/transacional (o que o seller faz todo dia)
// CRESCIMENTO  — ferramentas que geram venda (links, marketplace, splits)
// API          — integração técnica (dedicated section pra devs, padrão Stripe)
// CONTA        — identidade, time, config geral
//
// Naming: "Links de Pagamento" e "Regras de Split" em PT (eram em inglês).
// "Webhooks" mantido em inglês (termo técnico padronizado, igual em qualquer
// SaaS de payments).
const SECTIONS: NavSection[] = [
  {
    id: "start",
    label: "Início",
    items: [
      { icon: <DashboardIcon />, name: "Painel", path: "/" },
      { icon: <InsightsIcon />, name: "Insights", path: "/insights" },
    ],
  },
  {
    id: "operation",
    label: "Operação",
    items: [
      { icon: <TransactionsIcon />, name: "Transações", path: "/transactions" },
      { icon: <RefundsIcon />, name: "Reembolsos", path: "/refunds" },
      { icon: <PayoutsIcon />, name: "Saques", path: "/payouts" },
      { icon: <SubscriptionsIcon />, name: "Assinaturas", path: "/subscriptions" },
      { icon: <ReceiptsIcon />, name: "Recibos", path: "/receipts" },
    ],
  },
  {
    id: "growth",
    label: "Crescimento",
    items: [
      { icon: <PaymentLinksIcon />, name: "Links de Pagamento", path: "/payment-links" },
      // Marketplace agrupa 4 sub-rotas relacionadas (meus produtos, catálogo
      // para me afiliar, minhas afiliações, cupons). subItems expandem no
      // click; o parent destaca quando qualquer filha está ativa via
      // pathname.startsWith.
      {
        icon: <MarketplaceIcon />,
        name: "Marketplace",
        subItems: [
          { name: "Meus Produtos", path: "/products", icon: <ProductsIcon /> },
          { name: "Catálogo (afiliar)", path: "/affiliate-marketplace", icon: <MarketplaceIcon /> },
          { name: "Minhas Afiliações", path: "/affiliations", icon: <AffiliationsIcon /> },
          { name: "Cupons", path: "/coupons", icon: <CouponsIcon /> },
        ],
      },
      { icon: <SplitIcon />, name: "Regras de Split", path: "/split-rules" },
      { icon: <SimulatorIcon />, name: "Simulador de Split", path: "/split-simulator" },
    ],
  },
  {
    id: "api",
    label: "API",
    items: [
      // "Integrações" unifica o que antes era /webhooks (tenant-wide dev API)
      // e /integrations (producer-scoped marketing). Pro seller eram a
      // mesma coisa visualmente — fundido pra reduzir ambiguidade. /webhooks
      // mantém-se como redirect 301 → /integrations pra cobrir bookmarks.
      { icon: <IntegrationsIcon />, name: "Integrações", path: "/integrations" },
      // Próximos itens da seção: Chaves de API, Logs/Eventos, Sandbox.
    ],
  },
  // Seção "Conta" (Meu Nível, Equipe, Configurações) saiu do sidebar e foi
  // pro UserDropdown do header — pattern Linear/Notion/Vercel: ações de
  // conta/admin no menu do usuário, sidebar foca em workspace/operação.
  // Libera espaço vertical pro sidebar não pedir scroll em laptops.
];

const AppSidebar: React.FC = () => {
  const { isExpanded, isMobileOpen, isHovered, setIsHovered } = useSidebar();
  const pathname = usePathname();

  // Ref/state pra pill flutuante (indicator) que desliza entre itens ativos
  // do menu. Mesma técnica do SegmentedPresets — mede DOM via ref, anima
  // left/top/width/height por CSS transition.
  const navRef = useRef<HTMLElement | null>(null);
  const [navIndicator, setNavIndicator] = useState<{
    left: number;
    top: number;
    width: number;
    height: number;
    visible: boolean;
  }>({ left: 0, top: 0, width: 0, height: 0, visible: false });

  const renderMenuItems = (
    navItems: NavItem[],
    menuType: string
  ) => (
    <ul className="flex flex-col gap-0.5">
      {navItems.map((nav, index) => {
        const isCollapsed = !isExpanded && !isHovered && !isMobileOpen;
        // Computa o estado "ativo" do item — diferente entre Link (path-based)
        // e button (submenu open). Precisamos pra decidir o width do item
        // em modo colapsado.
        const isItemActive = nav.subItems
          ? openSubmenu?.type === menuType && openSubmenu?.index === index
          : nav.path
            ? isActive(nav.path)
            : false;

        // Em modo colapsado:
        // - Item INATIVO: w-9 = 32.4px square → footprint compacto, sem bg.
        // - Item ATIVO: w-12 = 43.2px (wider than h-9) → rounded-full vira
        //   pill horizontal "esticado", mimicando a proporção da versão
        //   expandida em escala reduzida. Mantém menu-item-active aplicado
        //   pra usar o bg + inset 1px border + glow já definidos lá.
        // !important pra vencer o w-full @apply'd pela utility menu-item.
        const collapsedClasses = isCollapsed
          ? `menu-item-is-collapsed lg:!mx-auto lg:!px-0 lg:justify-center ${
              isItemActive ? "lg:!w-12" : "lg:!w-9"
            }`
          : "";

        return (
        <li key={nav.name}>
          {nav.subItems ? (
            <button
              onClick={() => handleSubmenuToggle(index, menuType)}
              className={`menu-item group ${
                isItemActive ? "menu-item-active" : "menu-item-inactive"
              } cursor-pointer ${collapsedClasses}`}
            >
              <span
                className={`menu-item-icon ${
                  openSubmenu?.type === menuType && openSubmenu?.index === index
                    ? "menu-item-icon-active"
                    : "menu-item-icon-inactive"
                }`}
              >
                {nav.icon}
              </span>
              {(isExpanded || isHovered || isMobileOpen) && (
                <span className="menu-item-text">{nav.name}</span>
              )}
              {(isExpanded || isHovered || isMobileOpen) && (
                <ChevronDownIcon
                  className={`ml-auto w-5 h-5 transition-transform duration-200 ${
                    openSubmenu?.type === menuType &&
                    openSubmenu?.index === index
                      ? "rotate-180 text-brand-500"
                      : ""
                  }`}
                />
              )}
            </button>
          ) : (
            nav.path && (
              <Link
                href={nav.path}
                className={`menu-item group ${
                  isItemActive ? "menu-item-active" : "menu-item-inactive"
                } ${collapsedClasses}`}
              >
                <span
                  className={`menu-item-icon ${
                    isActive(nav.path)
                      ? "menu-item-icon-active"
                      : "menu-item-icon-inactive"
                  }`}
                >
                  {nav.icon}
                </span>
                {(isExpanded || isHovered || isMobileOpen) && (
                  <span className="menu-item-text">{nav.name}</span>
                )}
              </Link>
            )
          )}
          {nav.subItems && (isExpanded || isHovered || isMobileOpen) && (
            <div
              ref={(el) => {
                subMenuRefs.current[`${menuType}-${index}`] = el;
              }}
              className="overflow-hidden transition-all duration-300"
              style={{
                height:
                  openSubmenu?.type === menuType && openSubmenu?.index === index
                    ? `${subMenuHeight[`${menuType}-${index}`]}px`
                    : "0px",
              }}
            >
              <ul className="mt-2 space-y-1 ml-9">
                {nav.subItems.map((subItem) => (
                  <li key={subItem.name}>
                    <Link
                      href={subItem.path}
                      className={`menu-dropdown-item flex items-center gap-2 ${
                        isActive(subItem.path)
                          ? "menu-dropdown-item-active"
                          : "menu-dropdown-item-inactive"
                      }`}
                    >
                      {/* Ícone opcional do subitem — quando presente, vem antes
                          do label. `[&_svg]:w-4` força tamanho menor pra não
                          competir visualmente com os ícones dos itens top-level
                          (que herdam tamanho do container). */}
                      {subItem.icon && (
                        <span className="inline-flex items-center justify-center w-4 h-4 shrink-0 [&_svg]:w-4 [&_svg]:h-4">
                          {subItem.icon}
                        </span>
                      )}
                      <span>{subItem.name}</span>
                    </Link>
                  </li>
                ))}
              </ul>
            </div>
          )}
        </li>
        );
      })}
    </ul>
  );

  const [openSubmenu, setOpenSubmenu] = useState<{
    type: string;
    index: number;
  } | null>(null);
  const [subMenuHeight, setSubMenuHeight] = useState<Record<string, number>>(
    {}
  );
  const subMenuRefs = useRef<Record<string, HTMLDivElement | null>>({});

  const isActive = useCallback((path: string) => path === pathname, [pathname]);

  useEffect(() => {
    let submenuMatched = false;
    SECTIONS.forEach((section) => {
      section.items.forEach((nav, index) => {
        if (nav.subItems) {
          nav.subItems.forEach((subItem) => {
            // `startsWith` em vez de `isActive` (exact match) — assim o grupo
            // continua expandido quando o usuário está em rotas filhas tipo
            // /products/abc-123 ou /affiliations/xyz. Sem isso o menu colapsa
            // ao entrar em detalhe, confundindo navegação. Edge case: subItem
            // path "/" daria match em tudo, mas o root nunca é subitem.
            if (pathname === subItem.path || pathname.startsWith(subItem.path + "/")) {
              setOpenSubmenu({ type: section.id, index });
              submenuMatched = true;
            }
          });
        }
      });
    });
    if (!submenuMatched) {
      setOpenSubmenu(null);
    }
  }, [pathname, isActive]);

  // Pill flutuante: mede o item ativo (.menu-item-active) e posiciona o
  // indicator. Re-roda quando a rota muda (active flipa pra outro item) ou
  // quando a sidebar colapsa/expande (item ativo muda de largura).
  useLayoutEffect(() => {
    const nav = navRef.current;
    if (!nav) return;
    const measure = () => {
      const active = nav.querySelector<HTMLElement>(".menu-item-active");
      if (!active) {
        setNavIndicator((s) => ({ ...s, visible: false }));
        return;
      }
      // offsetTop/Left são relativos ao parent positioned (nav tem `relative`),
      // logo já dão a posição correta dentro do nav sem precisar calcular
      // rect diffs (mais rápido e estável durante scroll).
      setNavIndicator({
        left: active.offsetLeft,
        top: active.offsetTop,
        width: active.offsetWidth,
        height: active.offsetHeight,
        visible: true,
      });
    };
    measure();
    // Resize observer pega mudanças de largura (sidebar expandindo de w-9 → w-full)
    // sem precisar de evento window.
    const ro = new ResizeObserver(measure);
    ro.observe(nav);
    window.addEventListener("resize", measure);
    return () => {
      ro.disconnect();
      window.removeEventListener("resize", measure);
    };
  }, [pathname, isExpanded, isHovered, isMobileOpen]);

  useEffect(() => {
    if (openSubmenu !== null) {
      const key = `${openSubmenu.type}-${openSubmenu.index}`;
      if (subMenuRefs.current[key]) {
        setSubMenuHeight((prevHeights) => ({
          ...prevHeights,
          [key]: subMenuRefs.current[key]?.scrollHeight || 0,
        }));
      }
    }
  }, [openSubmenu]);

  const handleSubmenuToggle = (index: number, menuType: string) => {
    setOpenSubmenu((prevOpenSubmenu) => {
      if (
        prevOpenSubmenu &&
        prevOpenSubmenu.type === menuType &&
        prevOpenSubmenu.index === index
      ) {
        return null;
      }
      return { type: menuType, index };
    });
  };

  return (
    <aside
      className={`fixed mt-16 flex flex-col lg:mt-0 top-0 px-4 left-0 bg-white dark:bg-gray-900 dark:border-gray-800 text-gray-900 h-screen transition-all duration-300 ease-in-out z-50 border-r border-gray-200
        ${
          isExpanded || isMobileOpen
            ? "w-[270px]"
            : isHovered
            ? "w-[270px]"
            : "w-[80px]"
        }
        ${isMobileOpen ? "translate-x-0" : "-translate-x-full"}
        lg:translate-x-0`}
      onMouseEnter={() => !isExpanded && setIsHovered(true)}
      onMouseLeave={() => setIsHovered(false)}
    >
      <div
        className={`py-6 flex ${
          !isExpanded && !isHovered ? "lg:justify-center" : "justify-start"
        }`}
      >
        <Link href="/" className="inline-flex items-center gap-2.5">
          <Image
            src="/images/fellow/fellow-pay-logo.PNG"
            alt="Fellow Pay"
            width={32}
            height={32}
            className="shrink-0"
            priority
          />
          {(isExpanded || isHovered || isMobileOpen) && (
            <span className="text-[18px] font-semibold tracking-tight text-gray-900 dark:text-white whitespace-nowrap">
              Fellow <span className="text-brand-500">Pay</span>
            </span>
          )}
        </Link>
      </div>
      <div className="flex flex-col overflow-y-auto duration-300 ease-linear no-scrollbar">
        <nav ref={navRef} className="relative mb-6">
          {/* Pill flutuante que desliza entre os itens ativos. Coordenadas
              calculadas via ref measurement (useLayoutEffect abaixo). */}
          <div
            className={`nav-active-indicator ${
              !isExpanded && !isHovered && !isMobileOpen
                ? "nav-active-indicator-collapsed"
                : ""
            }`}
            style={{
              left: navIndicator.left,
              top: navIndicator.top,
              width: navIndicator.width,
              height: navIndicator.height,
              opacity: navIndicator.visible ? 1 : 0,
            }}
            aria-hidden="true"
          />
          {/* gap-3 entre seções (era gap-6) — compensa o overhead vertical
              dos múltiplos grupos. Combinado com headers mb-1.5 e menu-item
              py-1.5, garante que tudo cabe no fold em viewports normais sem
              pedir scroll. */}
          <div className="flex flex-col gap-3">
            {SECTIONS.map((section) => (
              <div key={section.id}>
                <h2
                  className={`mb-1.5 text-[10px] uppercase flex leading-[16px] text-gray-400 font-semibold tracking-[0.08em] ${
                    !isExpanded && !isHovered
                      ? "lg:justify-center"
                      : "justify-start"
                  }`}
                >
                  {isExpanded || isHovered || isMobileOpen ? (
                    section.label
                  ) : (
                    <HorizontaLDots />
                  )}
                </h2>
                {renderMenuItems(section.items, section.id)}
              </div>
            ))}
          </div>
        </nav>
      </div>
    </aside>
  );
};

export default AppSidebar;
