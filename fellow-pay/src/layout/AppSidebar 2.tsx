"use client";
import React, { useEffect, useRef, useState, useCallback } from "react";
import Link from "next/link";
import Image from "next/image";
import { usePathname } from "next/navigation";
import { useSidebar } from "../context/SidebarContext";
import { ChevronDownIcon, HorizontaLDots } from "../icons/index";

type NavItem = {
  name: string;
  icon: React.ReactNode;
  path?: string;
  subItems?: { name: string; path: string }[];
};

// --- SVG Icons ---
const DashboardIcon = () => (
  <svg width="20" height="20" viewBox="0 0 20 20" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M2.5 10.833h6.667V2.5H2.5v8.333Zm0 6.667h6.667v-5H2.5v5Zm8.333 0H17.5v-8.333h-6.667V17.5Zm0-15v5H17.5V2.5h-6.667Z" fill="currentColor"/>
  </svg>
);

const InsightsIcon = () => (
  <svg width="20" height="20" viewBox="0 0 20 20" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M3 17V8m4 9V3m4 14v-6m4 6v-9" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/>
  </svg>
);

const TransactionsIcon = () => (
  <svg width="20" height="20" viewBox="0 0 20 20" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M3.333 5h13.334M3.333 10h13.334M3.333 15h13.334" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/>
    <circle cx="6.667" cy="5" r="1.25" fill="currentColor"/>
    <circle cx="13.333" cy="10" r="1.25" fill="currentColor"/>
    <circle cx="8.333" cy="15" r="1.25" fill="currentColor"/>
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
  <svg width="20" height="20" viewBox="0 0 20 20" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M8.333 11.667a4.167 4.167 0 0 0 5.892 0l2.5-2.5a4.167 4.167 0 0 0-5.892-5.892L9.583 4.525" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/>
    <path d="M11.667 8.333a4.167 4.167 0 0 0-5.892 0l-2.5 2.5a4.167 4.167 0 0 0 5.892 5.892l1.25-1.25" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/>
  </svg>
);

const SplitIcon = () => (
  <svg width="20" height="20" viewBox="0 0 20 20" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M10 2.5v6.667M10 9.167l5 5M10 9.167l-5 5" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"/>
    <circle cx="10" cy="2.5" r="1.25" fill="currentColor"/>
    <circle cx="15" cy="15" r="1.25" fill="currentColor"/>
    <circle cx="5" cy="15" r="1.25" fill="currentColor"/>
  </svg>
);

const SimulatorIcon = () => (
  <svg width="20" height="20" viewBox="0 0 20 20" fill="none" xmlns="http://www.w3.org/2000/svg">
    <rect x="2.5" y="3.333" width="15" height="13.333" rx="2" stroke="currentColor" strokeWidth="1.5"/>
    <path d="M5.833 7.5h2.5M5.833 10h4.167M5.833 12.5h3.333" stroke="currentColor" strokeWidth="1.25" strokeLinecap="round"/>
    <path d="M13.333 8.333l1.25 1.25-1.25 1.25" stroke="currentColor" strokeWidth="1.25" strokeLinecap="round" strokeLinejoin="round"/>
  </svg>
);

const PayoutsIcon = () => (
  <svg width="20" height="20" viewBox="0 0 20 20" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M17.5 5H2.5v11.667h15V5Z" stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round"/>
    <circle cx="10" cy="10.833" r="2.5" stroke="currentColor" strokeWidth="1.5"/>
    <path d="M5 5V3.333h10V5" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"/>
  </svg>
);

const RefundsIcon = () => (
  <svg width="20" height="20" viewBox="0 0 20 20" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M2.5 8.333h10a4.167 4.167 0 0 1 0 8.334H10" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"/>
    <path d="M5.833 5 2.5 8.333l3.333 3.334" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"/>
  </svg>
);

const DisputesIcon = () => (
  <svg width="20" height="20" viewBox="0 0 20 20" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M10 6.667V10M10 13.333h.008" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/>
    <path d="M8.575 3.217 2.517 13.75a1.667 1.667 0 0 0 1.425 2.5h12.116a1.667 1.667 0 0 0 1.425-2.5L11.425 3.217a1.667 1.667 0 0 0-2.85 0Z" stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round"/>
  </svg>
);

const SubscriptionsIcon = () => (
  <svg width="20" height="20" viewBox="0 0 20 20" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M16.667 6.667 10 10 3.333 6.667" stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round"/>
    <path d="M3.333 6.667v6.666L10 17.5l6.667-4.167V6.667L10 2.5 3.333 6.667Z" stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round"/>
    <path d="M10 10v7.5" stroke="currentColor" strokeWidth="1.5"/>
  </svg>
);

const ReceiptsIcon = () => (
  <svg width="20" height="20" viewBox="0 0 20 20" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M5 2.5h10a1.667 1.667 0 0 1 1.667 1.667v13.75l-2.5-1.667-2.5 1.667L10 16.25l-1.667 1.667-2.5-1.667-2.5 1.667V4.167A1.667 1.667 0 0 1 5 2.5Z" stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round"/>
    <path d="M7.5 7.5h5M7.5 10.833h5" stroke="currentColor" strokeWidth="1.25" strokeLinecap="round"/>
  </svg>
);

const WebhooksIcon = () => (
  <svg width="20" height="20" viewBox="0 0 20 20" fill="none" xmlns="http://www.w3.org/2000/svg">
    <circle cx="10" cy="5" r="2.5" stroke="currentColor" strokeWidth="1.5"/>
    <circle cx="5" cy="15" r="2.5" stroke="currentColor" strokeWidth="1.5"/>
    <circle cx="15" cy="15" r="2.5" stroke="currentColor" strokeWidth="1.5"/>
    <path d="M10 7.5v2.083l-3.75 3.334M10 9.583l3.75 3.334" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/>
  </svg>
);

const ReportsIcon = () => (
  <svg width="20" height="20" viewBox="0 0 20 20" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M5.833 14.167V10M10 14.167V7.5M14.167 14.167V5" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/>
    <rect x="2.5" y="2.5" width="15" height="15" rx="2" stroke="currentColor" strokeWidth="1.5"/>
  </svg>
);

const TeamIcon = () => (
  <svg width="20" height="20" viewBox="0 0 20 20" fill="none" xmlns="http://www.w3.org/2000/svg">
    <circle cx="7.5" cy="5.833" r="2.5" stroke="currentColor" strokeWidth="1.5"/>
    <circle cx="14.167" cy="7.5" r="2.083" stroke="currentColor" strokeWidth="1.5"/>
    <path d="M1.667 16.667v-1.25a4.167 4.167 0 0 1 4.167-4.167h3.333a4.167 4.167 0 0 1 4.167 4.167v1.25" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/>
    <path d="M14.167 10.833a3.333 3.333 0 0 1 3.333 3.334v1.666" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/>
  </svg>
);

const SettingsIcon = () => (
  <svg width="20" height="20" viewBox="0 0 20 20" fill="none" xmlns="http://www.w3.org/2000/svg">
    <circle cx="10" cy="10" r="2.5" stroke="currentColor" strokeWidth="1.5"/>
    <path d="M16.167 12.5a1.375 1.375 0 0 0 .275 1.517l.05.05a1.667 1.667 0 1 1-2.359 2.358l-.05-.05a1.375 1.375 0 0 0-1.516-.275 1.375 1.375 0 0 0-.834 1.258v.142a1.667 1.667 0 1 1-3.333 0v-.075a1.375 1.375 0 0 0-.9-1.258 1.375 1.375 0 0 0-1.517.275l-.05.05A1.667 1.667 0 1 1 3.575 14.133l.05-.05A1.375 1.375 0 0 0 3.9 12.567a1.375 1.375 0 0 0-1.258-.834H2.5a1.667 1.667 0 0 1 0-3.333h.075a1.375 1.375 0 0 0 1.258-.9 1.375 1.375 0 0 0-.275-1.517l-.05-.05A1.667 1.667 0 1 1 5.867 3.575l.05.05a1.375 1.375 0 0 0 1.516.275h.067a1.375 1.375 0 0 0 .833-1.258V2.5a1.667 1.667 0 0 1 3.334 0v.075a1.375 1.375 0 0 0 .833 1.258 1.375 1.375 0 0 0 1.517-.275l.05-.05a1.667 1.667 0 1 1 2.358 2.359l-.05.05a1.375 1.375 0 0 0-.275 1.516v.067a1.375 1.375 0 0 0 1.258.833H17.5a1.667 1.667 0 0 1 0 3.334h-.075a1.375 1.375 0 0 0-1.258.833Z" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"/>
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
const navItems: NavItem[] = [
  {
    icon: <DashboardIcon />,
    name: "Painel",
    path: "/",
  },
  {
    icon: <InsightsIcon />,
    name: "Insights",
    path: "/insights",
  },
  {
    icon: <TransactionsIcon />,
    name: "Transações",
    path: "/transactions",
  },
  {
    icon: <PaymentLinksIcon />,
    name: "Payment Links",
    path: "/payment-links",
  },
  {
    icon: <SplitIcon />,
    name: "Split Rules",
    path: "/split-rules",
  },
  {
    icon: <SimulatorIcon />,
    name: "Simulador de Split",
    path: "/split-simulator",
  },
  {
    icon: <PayoutsIcon />,
    name: "Saques",
    path: "/payouts",
  },
  {
    icon: <RefundsIcon />,
    name: "Reembolsos",
    path: "/refunds",
  },
  {
    icon: <SubscriptionsIcon />,
    name: "Assinaturas",
    path: "/subscriptions",
  },
];

const othersItems: NavItem[] = [
  {
    icon: <ReceiptsIcon />,
    name: "Recibos",
    path: "/receipts",
  },
  {
    icon: <TeamIcon />,
    name: "Equipe",
    path: "/team",
  },
  {
    icon: <WebhooksIcon />,
    name: "Webhooks",
    path: "/webhooks",
  },
  {
    icon: <SettingsIcon />,
    name: "Configurações",
    path: "/settings",
  },
];

const AppSidebar: React.FC = () => {
  const { isExpanded, isMobileOpen, isHovered, setIsHovered } = useSidebar();
  const pathname = usePathname();

  const renderMenuItems = (
    navItems: NavItem[],
    menuType: "main" | "others"
  ) => (
    <ul className="flex flex-col gap-1">
      {navItems.map((nav, index) => (
        <li key={nav.name}>
          {nav.subItems ? (
            <button
              onClick={() => handleSubmenuToggle(index, menuType)}
              className={`menu-item group ${
                openSubmenu?.type === menuType && openSubmenu?.index === index
                  ? "menu-item-active"
                  : "menu-item-inactive"
              } cursor-pointer ${
                !isExpanded && !isHovered
                  ? "lg:justify-center"
                  : "lg:justify-start"
              }`}
            >
              <span
                className={`${
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
                  isActive(nav.path) ? "menu-item-active" : "menu-item-inactive"
                }`}
              >
                <span
                  className={`${
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
                      className={`menu-dropdown-item ${
                        isActive(subItem.path)
                          ? "menu-dropdown-item-active"
                          : "menu-dropdown-item-inactive"
                      }`}
                    >
                      {subItem.name}
                    </Link>
                  </li>
                ))}
              </ul>
            </div>
          )}
        </li>
      ))}
    </ul>
  );

  const [openSubmenu, setOpenSubmenu] = useState<{
    type: "main" | "others";
    index: number;
  } | null>(null);
  const [subMenuHeight, setSubMenuHeight] = useState<Record<string, number>>(
    {}
  );
  const subMenuRefs = useRef<Record<string, HTMLDivElement | null>>({});

  const isActive = useCallback((path: string) => path === pathname, [pathname]);

  useEffect(() => {
    let submenuMatched = false;
    ["main", "others"].forEach((menuType) => {
      const items = menuType === "main" ? navItems : othersItems;
      items.forEach((nav, index) => {
        if (nav.subItems) {
          nav.subItems.forEach((subItem) => {
            if (isActive(subItem.path)) {
              setOpenSubmenu({
                type: menuType as "main" | "others",
                index,
              });
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

  const handleSubmenuToggle = (index: number, menuType: "main" | "others") => {
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
        <Link href="/">
          {isExpanded || isHovered || isMobileOpen ? (
            <>
              {/* Mesma arte de logo em ambos os modos (a do checkout público).
                  No dark mode envolvemos num "chip" branco arredondado pra que o
                  texto escuro do PNG continue legível em fundo escuro. */}
              <Image
                className="dark:hidden"
                src="/images/fellow/fellow-pay-full-logo-no-bg-light-mode.png"
                alt="Fellow Pay"
                width={140}
                height={36}
              />
              <span className="hidden dark:inline-flex bg-white rounded-lg px-2 py-1">
                <Image
                  src="/images/fellow/fellow-pay-full-logo-no-bg-light-mode.png"
                  alt="Fellow Pay"
                  width={130}
                  height={32}
                />
              </span>
            </>
          ) : (
            <Image
              src="/images/fellow/fellow-pay-logo.PNG"
              alt="Fellow Pay"
              width={32}
              height={32}
            />
          )}
        </Link>
      </div>
      <div className="flex flex-col overflow-y-auto duration-300 ease-linear no-scrollbar">
        <nav className="mb-6">
          <div className="flex flex-col gap-6">
            <div>
              <h2
                className={`mb-3 text-xs uppercase flex leading-[20px] text-gray-400 font-semibold tracking-wider ${
                  !isExpanded && !isHovered
                    ? "lg:justify-center"
                    : "justify-start"
                }`}
              >
                {isExpanded || isHovered || isMobileOpen ? (
                  "Vendas"
                ) : (
                  <HorizontaLDots />
                )}
              </h2>
              {renderMenuItems(navItems, "main")}
            </div>

            <div>
              <h2
                className={`mb-3 text-xs uppercase flex leading-[20px] text-gray-400 font-semibold tracking-wider ${
                  !isExpanded && !isHovered
                    ? "lg:justify-center"
                    : "justify-start"
                }`}
              >
                {isExpanded || isHovered || isMobileOpen ? (
                  "Conta"
                ) : (
                  <HorizontaLDots />
                )}
              </h2>
              {renderMenuItems(othersItems, "others")}
            </div>
          </div>
        </nav>
      </div>
    </aside>
  );
};

export default AppSidebar;
