"use client";
import Link from "next/link";
import React, { useState } from "react";
import { Dropdown } from "../ui/dropdown/Dropdown";
import { DropdownItem } from "../ui/dropdown/DropdownItem";
import { LiquidGlassSurface } from "@/components/ui/LiquidGlassSurface";
import { useTheme } from "@/context/ThemeContext";
import { useAuth } from "@/context/AuthContext";
import { useSellerProfile } from "@/hooks/useSellerProfile";
import { useSellerTier } from "@/hooks/useSellerTier";
import { TierBadge, FoundingBadge } from "@/components/tier/TierBadge";

function initials(name: string): string {
  const parts = name.trim().split(/\s+/);
  if (parts.length === 0 || !parts[0]) return "?";
  const first = parts[0]?.[0] ?? "";
  const last = parts.length > 1 ? parts[parts.length - 1][0] : "";
  return (first + last).toUpperCase() || "?";
}

export default function UserDropdown() {
  const [isOpen, setIsOpen] = useState(false);
  const { user, logout } = useAuth();
  // Tint do liquid glass condicional pelo tema — branco translúcido no
  // light mode, gray-900 translúcido no dark. Sem isso, dark mode renderiza
  // o body do popup com tint branco sobre fundo escuro, ficando "luminoso"
  // e clashando com o resto do app.
  const { theme } = useTheme();
  const glassTint =
    theme === "dark" ? "rgba(46,26,79,0.85)" : "rgba(255,255,255,0.92)";
  // Profile traz nome fantasia + razão social — preferimos sobre o JWT name (que é
  // o nome da pessoa que loga, mas no header queremos mostrar o seller).
  const { profile } = useSellerProfile();
  // Tier driva os badges no header. Falha silenciosa pra usuários platform-only
  // (sem seller_id no JWT) — `tier` fica null, badges não renderizam.
  const { tier } = useSellerTier();

  function toggleDropdown(e: React.MouseEvent<HTMLButtonElement, MouseEvent>) {
    e.stopPropagation();
    setIsOpen((prev) => !prev);
  }

  function closeDropdown() {
    setIsOpen(false);
  }

  function handleLogout(e: React.MouseEvent<HTMLAnchorElement>) {
    e.preventDefault();
    closeDropdown();
    logout();
  }

  // Display name precedence: trade name → legal name → user.name → email local-part.
  const displayName =
    profile?.tradeName?.trim() ||
    profile?.legalName ||
    user?.name ||
    user?.email?.split("@")[0] ||
    "Conta";

  // Label completa do seller no botão do header. Antes pegávamos só a primeira
  // palavra ("Loja do Bruce Wayne" → "Loja"), o que criava ambiguidade quando
  // há múltiplas contas tipo "Loja X" / "Loja Y". Agora mostramos o nome
  // completo até `max-w-[220px]` e truncamos visualmente com ellipsis pra
  // nomes muito longos.
  const subtitle = user?.email ?? profile?.email ?? "";

  return (
    <div className="relative">
      <button
        onClick={toggleDropdown}
        className="dropdown-toggle relative overflow-hidden flex items-center gap-2 h-10 rounded-full pl-1 pr-4 pill-gradient-brand text-white transition-all"
      >
        {/* Shine sutil no topo — gradient branco-pra-transparente cobrindo
            metade superior do pill. Simula luz batendo na curvatura, padrão
            iOS glass / premium button. overflow-hidden no parent clipa pra
            forma do pill, pointer-events-none deixa o clique passar. */}
        <span
          aria-hidden="true"
          className="pointer-events-none absolute inset-x-0 top-0 h-1/2 bg-gradient-to-b from-white/25 to-transparent"
        />

        {/* Avatar BRANCO sobre bg purple — inversão de cor que cria pop:
            o purple do tema fica no container, o avatar é o "highlight"
            claro. Pattern de premium chip (Apple Pay button, Stripe premium
            tier indicators). */}
        <span className="relative inline-flex items-center justify-center h-8 w-8 rounded-full bg-white text-brand-600 text-xs font-bold shadow-inner">
          {initials(displayName)}
        </span>

        <span
          className="relative font-normal text-theme-sm text-white truncate max-w-[180px]"
          title={displayName}
        >
          {displayName}
        </span>

        {/* Badges (Tier + Founding) saíram do trigger — competiam visualmente
            com avatar (circular) e pareciam um "desfile de coisas diferentes".
            Agora ficam dentro do dropdown, onde têm espaço próprio e fazem
            sentido como info do menu (não como decoração do trigger). */}

        <svg
          className={`relative stroke-white/80 transition-transform duration-200 ${
            isOpen ? "rotate-180" : ""
          }`}
          width="14"
          height="14"
          viewBox="0 0 18 20"
          fill="none"
          xmlns="http://www.w3.org/2000/svg"
        >
          <path
            d="M4.3125 8.65625L9 13.3437L13.6875 8.65625"
            stroke="currentColor"
            strokeWidth="1.5"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        </svg>
      </button>

      <Dropdown
        isOpen={isOpen}
        onClose={closeDropdown}
        className="absolute right-0 mt-2 flex w-[280px] flex-col rounded-2xl overflow-hidden border border-brand-200/60 shadow-lg shadow-brand-500/20 dark:border-brand-500/30 dark:shadow-black/40"
      >
        {/* Header purple — extensão visual da pill. Mesmo gradient + texto
            branco. Os badges Tier + Pioneiro ficam aqui também, como info
            contextual da identidade. Cria a sensação de "select estendido". */}
        <div className="relative overflow-hidden bg-gradient-to-br from-brand-500 to-brand-700 px-4 pt-3.5 pb-4 text-white">
          <span
            aria-hidden="true"
            className="pointer-events-none absolute inset-x-0 top-0 h-1/2 bg-gradient-to-b from-white/15 to-transparent"
          />
          <span className="relative z-10 block font-semibold text-theme-sm truncate">
            {displayName}
          </span>
          <span className="relative z-10 mt-0.5 block text-theme-xs text-white/75 truncate">
            {subtitle}
          </span>
          {tier && (
            <div className="relative z-10 mt-2.5 flex items-center gap-1.5">
              <TierBadge tier={tier.currentTier} size="sm" />
              {tier.isFoundingSeller && tier.foundingNumber != null && (
                <FoundingBadge number={tier.foundingNumber} size="sm" />
              )}
            </div>
          )}
        </div>

        {/* Body com liquid glass effect — header roxo acima mantém solid,
            mas o body ganha o blur+distortion+shine via LiquidGlassSurface.
            Tint branco 0.85 dá legibilidade pros itens; subtle=true evita
            shadow/shine exagerado (o outer já tem shadow próprio). */}
        <LiquidGlassSurface
          rounded="rounded-none"
          bounce={false}
          subtle
          tint={glassTint}
          className="flex flex-col"
        >
        <ul className="flex flex-col gap-1 px-3 pt-3 pb-3 border-b border-gray-100 dark:border-gray-800">
          {/* Itens administrativos do user — saíram do sidebar pra cá pattern
              Linear/Notion: workspace stuff fica no sidebar, account stuff
              fica no menu do user. Cada item é uma rota real distinta.
              Ordem: Perfil (dados do próprio user) → Meu Nível (status) →
              Equipe (workspace people) → Configurações (config geral). */}
          <li>
            <DropdownItem
              onItemClick={closeDropdown}
              tag="a"
              href="/profile"
              className="flex items-center gap-3 px-3 py-2 font-medium text-gray-700 rounded-lg group text-theme-sm hover:bg-gray-100 hover:text-gray-700 dark:text-gray-400 dark:hover:bg-white/5 dark:hover:text-gray-300"
            >
              <svg
                className="stroke-gray-500 group-hover:stroke-gray-700 dark:stroke-gray-400 dark:group-hover:stroke-gray-300"
                width="20"
                height="20"
                viewBox="0 0 20 20"
                fill="none"
                xmlns="http://www.w3.org/2000/svg"
                aria-hidden="true"
              >
                <circle cx="10" cy="6.667" r="3.333" strokeWidth="1.5" />
                <path d="M3.333 17.5c0-3.682 2.985-6.667 6.667-6.667s6.667 2.985 6.667 6.667" strokeWidth="1.5" strokeLinecap="round" />
              </svg>
              Perfil
            </DropdownItem>
          </li>
          <li>
            <DropdownItem
              onItemClick={closeDropdown}
              tag="a"
              href="/tier"
              className="flex items-center gap-3 px-3 py-2 font-medium text-gray-700 rounded-lg group text-theme-sm hover:bg-gray-100 hover:text-gray-700 dark:text-gray-400 dark:hover:bg-white/5 dark:hover:text-gray-300"
            >
              <svg
                className="stroke-gray-500 group-hover:stroke-gray-700 dark:stroke-gray-400 dark:group-hover:stroke-gray-300"
                width="20"
                height="20"
                viewBox="0 0 20 20"
                fill="none"
                xmlns="http://www.w3.org/2000/svg"
                aria-hidden="true"
              >
                <path d="M10 1.667l2.45 4.96 5.476.797-3.963 3.864.936 5.459L10 14.167l-4.9 2.58.936-5.459L2.073 7.424l5.477-.797L10 1.667Z" strokeWidth="1.5" strokeLinejoin="round" />
              </svg>
              Meu Nível
            </DropdownItem>
          </li>
          <li>
            <DropdownItem
              onItemClick={closeDropdown}
              tag="a"
              href="/team"
              className="flex items-center gap-3 px-3 py-2 font-medium text-gray-700 rounded-lg group text-theme-sm hover:bg-gray-100 hover:text-gray-700 dark:text-gray-400 dark:hover:bg-white/5 dark:hover:text-gray-300"
            >
              <svg
                className="stroke-gray-500 group-hover:stroke-gray-700 dark:stroke-gray-400 dark:group-hover:stroke-gray-300"
                width="20"
                height="20"
                viewBox="0 0 20 20"
                fill="none"
                xmlns="http://www.w3.org/2000/svg"
                aria-hidden="true"
              >
                <path d="M13.333 17.5v-1.667a3.333 3.333 0 0 0-3.333-3.333H5a3.333 3.333 0 0 0-3.333 3.333V17.5" strokeWidth="1.5" strokeLinecap="round" />
                <circle cx="7.5" cy="5.833" r="3.333" strokeWidth="1.5" />
                <path d="M18.333 17.5v-1.667a3.333 3.333 0 0 0-2.5-3.225M13.333 2.608a3.333 3.333 0 0 1 0 6.459" strokeWidth="1.5" strokeLinecap="round" />
              </svg>
              Equipe
            </DropdownItem>
          </li>
          <li>
            <DropdownItem
              onItemClick={closeDropdown}
              tag="a"
              href="/settings"
              className="flex items-center gap-3 px-3 py-2 font-medium text-gray-700 rounded-lg group text-theme-sm hover:bg-gray-100 hover:text-gray-700 dark:text-gray-400 dark:hover:bg-white/5 dark:hover:text-gray-300"
            >
              <svg
                className="stroke-gray-500 group-hover:stroke-gray-700 dark:stroke-gray-400 dark:group-hover:stroke-gray-300"
                width="20"
                height="20"
                viewBox="0 0 20 20"
                fill="none"
                xmlns="http://www.w3.org/2000/svg"
                aria-hidden="true"
              >
                <circle cx="10" cy="10" r="3" strokeWidth="1.5" />
                <path d="M16.5 10a6.5 6.5 0 0 0-.13-1.293l1.45-1.135-1.5-2.598-1.726.642a6.5 6.5 0 0 0-2.24-1.293L11.78 2.5h-3l-.574 1.823a6.5 6.5 0 0 0-2.24 1.293l-1.726-.642-1.5 2.598L4.19 8.707A6.5 6.5 0 0 0 4.06 10c0 .44.045.872.13 1.293L2.74 12.428l1.5 2.598 1.726-.642a6.5 6.5 0 0 0 2.24 1.293L8.78 17.5h3l.574-1.823a6.5 6.5 0 0 0 2.24-1.293l1.726.642 1.5-2.598-1.45-1.135c.085-.42.13-.852.13-1.293Z" strokeWidth="1.5" strokeLinejoin="round" />
              </svg>
              Configurações
            </DropdownItem>
          </li>
        </ul>
        <Link
          href="/signin"
          onClick={handleLogout}
          className="flex items-center gap-3 px-3 py-2 mx-3 my-3 font-medium text-gray-700 rounded-lg group text-theme-sm hover:bg-gray-100 hover:text-gray-700 dark:text-gray-400 dark:hover:bg-white/5 dark:hover:text-gray-300"
        >
          <svg
            className="fill-gray-500 group-hover:fill-gray-700 dark:group-hover:fill-gray-300"
            width="24"
            height="24"
            viewBox="0 0 24 24"
            fill="none"
            xmlns="http://www.w3.org/2000/svg"
          >
            <path
              fillRule="evenodd"
              clipRule="evenodd"
              d="M15.1007 19.247C14.6865 19.247 14.3507 18.9112 14.3507 18.497L14.3507 14.245H12.8507V18.497C12.8507 19.7396 13.8581 20.747 15.1007 20.747H18.5007C19.7434 20.747 20.7507 19.7396 20.7507 18.497L20.7507 5.49609C20.7507 4.25345 19.7433 3.24609 18.5007 3.24609H15.1007C13.8581 3.24609 12.8507 4.25345 12.8507 5.49609V9.74501L14.3507 9.74501V5.49609C14.3507 5.08188 14.6865 4.74609 15.1007 4.74609L18.5007 4.74609C18.9149 4.74609 19.2507 5.08188 19.2507 5.49609L19.2507 18.497C19.2507 18.9112 18.9149 19.247 18.5007 19.247H15.1007ZM3.25073 11.9984C3.25073 12.2144 3.34204 12.4091 3.48817 12.546L8.09483 17.1556C8.38763 17.4485 8.86251 17.4487 9.15549 17.1559C9.44848 16.8631 9.44863 16.3882 9.15583 16.0952L5.81116 12.7484L16.0007 12.7484C16.4149 12.7484 16.7507 12.4127 16.7507 11.9984C16.7507 11.5842 16.4149 11.2484 16.0007 11.2484L5.81528 11.2484L9.15585 7.90554C9.44864 7.61255 9.44847 7.13767 9.15547 6.84488C8.86248 6.55209 8.3876 6.55226 8.09481 6.84525L3.52309 11.4202C3.35673 11.5577 3.25073 11.7657 3.25073 11.9984Z"
              fill=""
            />
          </svg>
          Sair
        </Link>
        </LiquidGlassSurface>
      </Dropdown>
    </div>
  );
}
