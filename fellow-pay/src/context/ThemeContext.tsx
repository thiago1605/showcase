"use client";

import type React from "react";
import { createContext, useState, useContext, useEffect } from "react";
import { usePathname } from "next/navigation";

type Theme = "light" | "dark";

type ThemeContextType = {
  theme: Theme;
  toggleTheme: () => void;
  /** True quando a rota atual é light-locked — UI deve esconder/desabilitar
   *  o toggle de tema. */
  isLightLocked: boolean;
};

const ThemeContext = createContext<ThemeContextType | undefined>(undefined);

/**
 * Rotas que IGNORAM a preferência do usuário e renderizam sempre em light.
 *
 * Por quê:
 *  - `/signin`, `/forgot-password`, `/reset-password` — telas pré-autenticação.
 *    Forms pedem contraste claro pra trust + acessibilidade (mensagens de erro
 *    em vermelho ficam ilegíveis sobre dark-bg vermelho-saturado).
 *  - `/pay/*` (checkout via payment link) e `/p/*` (checkout via produto) —
 *    indústria inteira (Stripe, Apple Pay, Shopify, Mercado Pago) renderiza
 *    checkout em light: trust signals (cadeado, bandeiras, SSL badges) foram
 *    desenhados pra fundo branco; testes A/B mostram queda de conversão em
 *    dark. Além disso o checkout é consumido pelo cliente do seller — não
 *    temos preferência salva dele pra honrar.
 *
 * Os regex usam `/` no final pra evitar falsos positivos com rotas admin
 * que começam parecido (ex.: `/payment-links` não deve matchear `/pay`).
 */
const LIGHT_LOCKED_PATTERNS: RegExp[] = [
  /^\/signin(\/|$)/,
  /^\/forgot-password(\/|$)/,
  /^\/reset-password(\/|$)/,
  /^\/pay\//,
  /^\/p\//,
];

function pathIsLightLocked(pathname: string | null): boolean {
  if (!pathname) return false;
  return LIGHT_LOCKED_PATTERNS.some((rx) => rx.test(pathname));
}

export const ThemeProvider: React.FC<{ children: React.ReactNode }> = ({
  children,
}) => {
  const [theme, setTheme] = useState<Theme>("light");
  const [isInitialized, setIsInitialized] = useState(false);
  const pathname = usePathname();
  const isLightLocked = pathIsLightLocked(pathname);

  useEffect(() => {
    // Carrega preferência salva — uma vez no mount. Default light.
    const savedTheme = localStorage.getItem("theme") as Theme | null;
    const initialTheme = savedTheme || "light";
    setTheme(initialTheme);
    setIsInitialized(true);
  }, []);

  useEffect(() => {
    if (!isInitialized) return;
    // Persiste a preferência (mesmo em rota light-locked) pra quando o
    // usuário voltar pro app a escolha dele esteja preservada.
    localStorage.setItem("theme", theme);

    // Tema efetivo: rota light-locked sobrescreve a preferência salva.
    const effectiveTheme: Theme = isLightLocked ? "light" : theme;
    if (effectiveTheme === "dark") {
      document.documentElement.classList.add("dark");
    } else {
      document.documentElement.classList.remove("dark");
    }
  }, [theme, isInitialized, isLightLocked]);

  const toggleTheme = () => {
    // No-op em rotas light-locked. UI consumindo `isLightLocked` deve
    // esconder o toggle nessas telas; este guard é defesa em profundidade.
    if (isLightLocked) return;
    setTheme((prevTheme) => (prevTheme === "light" ? "dark" : "light"));
  };

  return (
    <ThemeContext.Provider value={{ theme, toggleTheme, isLightLocked }}>
      {children}
    </ThemeContext.Provider>
  );
};

export const useTheme = () => {
  const context = useContext(ThemeContext);
  if (context === undefined) {
    throw new Error("useTheme must be used within a ThemeProvider");
  }
  return context;
};
