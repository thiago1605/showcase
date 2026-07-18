"use client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useState } from "react";

/**
 * Provider de cache pra React Query — instanciado uma vez por sessão.
 * - staleTime 30s: dados financeiros mudam mas raramente em sub-segundos.
 * - refetchOnWindowFocus true: voltar para a aba revalida silenciosamente.
 * - retry 1: erros 4xx (auth/scope) não merecem retry; 1 tentativa cobre flap de rede.
 */
export function QueryProvider({ children }: { children: React.ReactNode }) {
  const [client] = useState(
    () =>
      new QueryClient({
        defaultOptions: {
          queries: {
            staleTime: 30 * 1000,
            gcTime: 5 * 60 * 1000,
            refetchOnWindowFocus: true,
            retry: 1,
          },
        },
      })
  );

  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}
