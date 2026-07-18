import React from "react";
import { describe, it, expect, vi } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import type { SellerProfile } from "@/types";
import AdvanceSettlementCard from "@/components/seller-profile/AdvanceSettlementCard";

function buildProfile(overrides: Partial<SellerProfile> = {}): SellerProfile {
  return {
    id: "seller-1",
    legalName: "Bruce Wayne",
    tradeName: "Wayne Enterprises",
    document: "12345678901",
    email: "b@x.com",
    mobilePhone: null,
    pixKey: null,
    status: "ACTIVE",
    preferredProvider: 0,
    externalAccountId: "acct_x",
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z",
    autoAdvanceSettlement: false,
    advanceCreditLimit: 0,
    advanceExposureCurrent: 0,
    ...overrides,
  };
}

describe("AdvanceSettlementCard", () => {
  it("renderiza estado desligado por default", () => {
    render(<AdvanceSettlementCard profile={buildProfile()} onSave={vi.fn()} />);

    const toggle = screen.getByRole("checkbox", { name: /Ativar antecipação automática/i });
    expect(toggle).not.toBeChecked();
  });

  it("renderiza estado ligado quando autoAdvanceSettlement=true", () => {
    render(<AdvanceSettlementCard profile={buildProfile({ autoAdvanceSettlement: true })} onSave={vi.fn()} />);

    const toggle = screen.getByRole("checkbox", { name: /Ativar antecipação automática/i });
    expect(toggle).toBeChecked();
  });

  it("desabilita toggle quando seller não tem limite aprovado", () => {
    render(<AdvanceSettlementCard profile={buildProfile({ advanceCreditLimit: 0 })} onSave={vi.fn()} />);

    const toggle = screen.getByRole("checkbox", { name: /Ativar antecipação automática/i });
    expect(toggle).toBeDisabled();

    expect(screen.getByText(/Antecipação ainda não disponível/i)).toBeInTheDocument();
  });

  it("mostra disponível e em uso quando há limite aprovado", () => {
    render(
      <AdvanceSettlementCard
        profile={buildProfile({ advanceCreditLimit: 10000, advanceExposureCurrent: 2500 })}
        onSave={vi.fn()}
      />
    );

    expect(screen.getByText("Disponível pra antecipar")).toBeInTheDocument();
    expect(screen.getByText(/R\$\s*7\.500,00/)).toBeInTheDocument(); // headroom = 10k - 2.5k

    expect(screen.getByText("Em uso (não recuperado)")).toBeInTheDocument();
    expect(screen.getByText(/R\$\s*2\.500,00/)).toBeInTheDocument();
  });

  it("chama onSave com novo valor ao togglar", async () => {
    const onSave = vi.fn().mockResolvedValue(buildProfile({ autoAdvanceSettlement: true }));
    const profile = buildProfile({ advanceCreditLimit: 10000 });

    render(<AdvanceSettlementCard profile={profile} onSave={onSave} />);

    const toggle = screen.getByRole("checkbox", { name: /Ativar antecipação automática/i });
    fireEvent.click(toggle);

    await waitFor(() => {
      expect(onSave).toHaveBeenCalledWith({ autoAdvanceSettlement: true });
    });
  });

  it("rollback optimistic quando onSave falha", async () => {
    const onSave = vi.fn().mockRejectedValue(new Error("network failure"));
    const profile = buildProfile({ advanceCreditLimit: 10000, autoAdvanceSettlement: false });

    render(<AdvanceSettlementCard profile={profile} onSave={onSave} />);

    const toggle = screen.getByRole("checkbox", { name: /Ativar antecipação automática/i });
    fireEvent.click(toggle);

    await waitFor(() => {
      expect(screen.getByText(/network failure/i)).toBeInTheDocument();
    });
    // Estado revertido pra unchecked
    expect(toggle).not.toBeChecked();
  });
});
