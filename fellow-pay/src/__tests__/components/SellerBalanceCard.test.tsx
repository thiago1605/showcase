import React from "react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import type { SellerBalance } from "@/types";

vi.mock("@/components/dashboard/Sparkline", () => ({
  Sparkline: ({ ariaLabel }: { ariaLabel?: string }) => (
    <div data-testid="sparkline" aria-label={ariaLabel} />
  ),
}));

vi.mock("next/link", () => ({
  default: ({ children, href, className }: { children: React.ReactNode; href: string; className?: string }) => (
    <a href={href} className={className}>
      {children}
    </a>
  ),
}));

vi.mock("@/services/dashboard.service", () => ({
  dashboardService: { getBalance: vi.fn() },
}));

import { dashboardService } from "@/services/dashboard.service";
import { SellerBalanceCard } from "@/components/dashboard/SellerBalanceCard";

describe("SellerBalanceCard", () => {
  beforeEach(() => vi.clearAllMocks());

  // Cenário Bruce real: 2k libera em D+2 (débito), 1.683 libera em D+180 (crédito 6x)
  // Stripe diz blocked=3.122 (561 de drift — reversal não conciliado).
  const bruceBalance: SellerBalance = {
    total: 3122,
    blocked: 3122,
    available: 0,
    isAccountReady: true,
    blockedByDate: [
      { releaseDate: "2026-05-15T00:00:00Z", amount: 2000 },
      { releaseDate: "2026-11-09T00:00:00Z", amount: 1683 },
    ],
    blockedBuckets: {
      next2Days: 2000,
      next7Days: 2000,
      next30Days: 2000,
      next90Days: 2000,
      next180Days: 3683,
      next365Days: 3683,
    },
  };

  it("renders the next release hint without expanding", async () => {
    vi.mocked(dashboardService.getBalance).mockResolvedValue(bruceBalance);

    render(<SellerBalanceCard />);

    await waitFor(() => {
      expect(screen.getByText(/Disponível para saque/i)).toBeInTheDocument();
    });

    // Hint compacto sempre visível — mostra a próxima janela com movimento
    expect(
      screen.getByText((content) =>
        /Próxima liberação.*2\.000,00.*em até 7 dias/i.test(content)
      )
    ).toBeInTheDocument();

    // Detalhe começa colapsado
    expect(screen.queryByText("De 3 a 6 meses")).not.toBeInTheDocument();
  });

  it("expands delta buckets (non-cumulative, sum to blocked) when expanded", async () => {
    vi.mocked(dashboardService.getBalance).mockResolvedValue(bruceBalance);

    render(<SellerBalanceCard />);

    const toggle = await screen.findByRole("button", { name: /Quando libera/i });
    fireEvent.click(toggle);

    // Janelas com movimento (não-zero) aparecem
    expect(screen.getByText("Em até 7 dias")).toBeInTheDocument();
    expect(screen.getByText("De 3 a 6 meses")).toBeInTheDocument();

    // Janelas zeradas (entre 8d e 90d) NÃO aparecem
    expect(screen.queryByText("De 8 a 30 dias")).not.toBeInTheDocument();
    expect(screen.queryByText("De 1 a 3 meses")).not.toBeInTheDocument();

    // R$ 2.000,00 aparece 2x (hint + bucket); R$ 1.683,00 aparece 1x (só bucket).
    expect(screen.getAllByText(/R\$\s*2\.000,00/).length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText(/R\$\s*1\.683,00/)).toBeInTheDocument();
  });

  it("shows drift row with explanation when schedule sum != blocked", async () => {
    vi.mocked(dashboardService.getBalance).mockResolvedValue(bruceBalance);

    render(<SellerBalanceCard />);
    fireEvent.click(await screen.findByRole("button", { name: /Quando libera/i }));

    // Bruce: blocked=3.122, sum(delta)=3.683 → drift = -561 (Stripe diz menos que prevemos)
    // Negativo → "Em conciliação" + explicação de reversal/liberação
    expect(screen.getByText("Em conciliação")).toBeInTheDocument();
    expect(screen.getByText(/Stripe estornou ou liberou parte do valor/i)).toBeInTheDocument();
  });

  it("positive drift labels as 'Sem data prevista'", async () => {
    // Cenário inverso: Stripe diz 5k bloqueado mas só temos 3.5k em previsão
    vi.mocked(dashboardService.getBalance).mockResolvedValue({
      ...bruceBalance,
      blocked: 5000,
      blockedBuckets: {
        next2Days: 0,
        next7Days: 3500,
        next30Days: 3500,
        next90Days: 3500,
        next180Days: 3500,
        next365Days: 3500,
      },
    });

    render(<SellerBalanceCard />);
    fireEvent.click(await screen.findByRole("button", { name: /Quando libera/i }));

    expect(screen.getByText("Sem data prevista")).toBeInTheDocument();
    expect(screen.getByText(/Saldo na Stripe sem TX correspondente/i)).toBeInTheDocument();
  });

  it("totals row always matches the official Bloqueado number", async () => {
    vi.mocked(dashboardService.getBalance).mockResolvedValue(bruceBalance);

    render(<SellerBalanceCard />);
    fireEvent.click(await screen.findByRole("button", { name: /Quando libera/i }));

    // Total bloqueado deve aparecer como linha de fechamento e bater com balance.blocked
    expect(screen.getByText("Total bloqueado")).toBeInTheDocument();
    // R$ 3.122,00 aparece 2x na DOM: no card principal e no total — usar getAllBy
    const totals = screen.getAllByText(/R\$\s*3\.122,00/);
    expect(totals.length).toBeGreaterThanOrEqual(2);
  });

  it("hides schedule UI when blocked is zero", async () => {
    vi.mocked(dashboardService.getBalance).mockResolvedValue({
      total: 5000,
      blocked: 0,
      available: 5000,
      isAccountReady: true,
    });

    render(<SellerBalanceCard />);

    await waitFor(() => {
      expect(screen.getByText(/R\$\s*5\.000,00/)).toBeInTheDocument();
    });

    expect(screen.queryByRole("button", { name: /Quando libera/i })).not.toBeInTheDocument();
    expect(screen.queryByText(/Próxima liberação/i)).not.toBeInTheDocument();
    expect(screen.queryByTestId("sparkline")).not.toBeInTheDocument();
  });

  it("shows error state when API call fails", async () => {
    vi.mocked(dashboardService.getBalance).mockRejectedValue(new Error("network"));

    render(<SellerBalanceCard />);

    await waitFor(() => {
      expect(screen.getByText(/network/)).toBeInTheDocument();
    });
  });
});
