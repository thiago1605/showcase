import { Inter, Outfit, Plus_Jakarta_Sans } from 'next/font/google';
import './globals.css';
import { SidebarProvider } from '@/context/SidebarContext';
import { ThemeProvider } from '@/context/ThemeContext';
import { AuthProvider } from '@/context/AuthContext';
import { ToastProvider } from '@/components/ui/Toast';
import { QueryProvider } from '@/lib/query/QueryProvider';
import { LiquidGlassFilter } from '@/components/ui/LiquidGlassFilter';
import { MagneticButtons } from '@/components/ui/MagneticButtons';

const inter = Inter({
  subsets: ["latin"],
  variable: "--font-inter",
});

// Outfit é a fonte de marca da Fellow Pay — usada nos charts e na identidade visual
// do banner de login. Carregada via next/font pra ficar otimizada e disponível como
// `font-display` no Tailwind (ver --font-display em globals.css).
const outfit = Outfit({
  subsets: ["latin"],
  weight: ["300", "400", "500", "600", "700"],
  variable: "--font-outfit",
});

// Plus Jakarta Sans — fonte do body adotada em 2026-05 pra alinhar com a
// estética Dokue (geométrica humanista, letterforms levemente arredondadas,
// muito usada em dashboards SaaS modernos). Substitui Inter como text padrão.
const plusJakarta = Plus_Jakarta_Sans({
  subsets: ["latin"],
  weight: ["300", "400", "500", "600", "700", "800"],
  variable: "--font-plus-jakarta",
});

export const metadata = {
  title: "Fellow Pay - Portal do Seller",
  description: "Portal operacional Fellow Pay - Grupo Fellow",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="pt-BR" suppressHydrationWarning className={`${inter.variable} ${outfit.variable} ${plusJakarta.variable}`}>
      <body className={`${plusJakarta.className} dark:bg-gray-950`} suppressHydrationWarning>
        {/* SVG filter global — referenciado por qualquer popup com efeito
            liquid glass via `filter: url(#fellow-liquid-glass)`. Montado
            uma única vez aqui pra existir no DOM enquanto o app vive. */}
        <LiquidGlassFilter />
        {/* Listener global do efeito magnético — atrai os botões `bg-brand-500`
            pelo cursor quando hover. Sem cost por-botão (event delegation
            no document). */}
        <MagneticButtons />
        <ThemeProvider>
          <QueryProvider>
            <AuthProvider>
              <ToastProvider>
                <SidebarProvider>{children}</SidebarProvider>
              </ToastProvider>
            </AuthProvider>
          </QueryProvider>
        </ThemeProvider>
      </body>
    </html>
  );
}
