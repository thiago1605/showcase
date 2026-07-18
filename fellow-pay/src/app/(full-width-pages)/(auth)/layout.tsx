import AuthOutdoorVideo from "@/components/auth/AuthOutdoorVideo";
import { ThemeProvider } from "@/context/ThemeContext";
import React from "react";

export default function AuthLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <div className="relative min-h-screen bg-[#000000]">
      <ThemeProvider>
        <div className="flex min-h-screen">
          {/* Outdoor — vídeo da logomarca Fellow Pay animada.
              Posicionado à esquerda no desktop. */}
          <div className="hidden lg:block relative overflow-hidden bg-[#6813bd] h-screen aspect-[832/1104] shrink-0 sticky top-0">
            <AuthOutdoorVideo />
          </div>

          {/* Form — fundo branco/escuro próprio pra não vazar o preto da camada de baixo. */}
          <div className="flex flex-col flex-1 justify-center px-6 py-12 sm:px-10 lg:px-16 bg-white dark:bg-gray-950">
            {children}
          </div>
        </div>
      </ThemeProvider>
    </div>
  );
}
