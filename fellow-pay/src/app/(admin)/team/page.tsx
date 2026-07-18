"use client";
import React, { useState } from "react";
import { useScrollLock } from "@/hooks/useScrollLock";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { teamService } from "@/services/team.service";
import { Select } from "@/components/ui/Select";
import { CardListSkeleton } from "@/components/ui/Skeleton";
import { DeleteButton } from "@/components/ui/DeleteButton";
import { ConfirmModal } from "@/components/ui/ConfirmModal";
import { PageHeader, PageHeaderButton } from "@/components/ui/PageHeader";
import Input from "@/components/form/input/InputField";
import type { TeamMember, UserRole } from "@/types";

const roleLabels: Record<string, string> = {
  OWNER: "Proprietário",
  DEVELOPER: "Desenvolvedor",
  FINANCE: "Financeiro",
  VIEWER: "Visualizador",
  SUPPORT: "Suporte",
};

const roleOptions: { value: UserRole; label: string }[] = [
  { value: "FINANCE", label: "Financeiro" },
  { value: "DEVELOPER", label: "Desenvolvedor" },
  { value: "SUPPORT", label: "Suporte" },
  { value: "VIEWER", label: "Visualizador" },
];

export default function TeamPage() {
  const queryClient = useQueryClient();
  const [showForm, setShowForm] = useState(false);
  const [formData, setFormData] = useState({ name: "", email: "", password: "", role: "FINANCE" as UserRole });
  const [formLoading, setFormLoading] = useState(false);
  const [formError, setFormError] = useState("");
  const [pending, setPending] = useState<{ id: string; name: string } | null>(null);
  const [pendingRunning, setPendingRunning] = useState(false);

  // Trava scroll da page enquanto qualquer modal estiver aberto (form de convite
  // ou ConfirmModal de remoção). ConfirmModal já trava sozinho via hook próprio;
  // aqui cobrimos o form de convite inline.
  useScrollLock(showForm);

  const { data: members = [], isLoading, error: queryError } = useQuery<TeamMember[]>({
    queryKey: ["team", "list"],
    queryFn: () => teamService.list(),
  });
  const loadError = queryError instanceof Error ? queryError.message : queryError ? "Não foi possível carregar a equipe." : null;
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["team"] });

  const handleInvite = async (e: React.FormEvent) => {
    e.preventDefault();
    setFormError("");
    setFormLoading(true);
    try {
      await teamService.invite(formData);
      setShowForm(false);
      setFormData({ name: "", email: "", password: "", role: "FINANCE" });
      invalidate();
    } catch (err) {
      setFormError(err instanceof Error ? err.message : "Erro ao convidar membro.");
    }
    setFormLoading(false);
  };

  const askRemove = (id: string, name: string) => {
    setPending({ id, name });
  };

  const runRemove = async () => {
    if (!pending) return;
    setPendingRunning(true);
    try {
      await teamService.remove(pending.id);
      invalidate();
      setPending(null);
    } catch { /* silently fail */ }
    setPendingRunning(false);
  };

  if (isLoading) {
    return (
      <CardListSkeleton count={4} ariaLabel="Carregando equipe" />
    );
  }

  return (
    <div className="space-y-6">
      <PageHeader
        size="hero"
        title="Equipe"
        subtitle="Gerencie membros e permissões de acesso."
        actions={
          <PageHeaderButton onClick={() => setShowForm(true)}>
            + Convidar membro
          </PageHeaderButton>
        }
        decorIcon={
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
            <circle cx="9" cy="7" r="3" />
            <path d="M3 21v-2a4 4 0 0 1 4-4h4a4 4 0 0 1 4 4v2" />
            <circle cx="17" cy="7" r="3" />
            <path d="M21 21v-2a4 4 0 0 0-3-3.87" />
          </svg>
        }
      />

      {loadError && (
        <div className="rounded-lg border border-error-200 bg-error-50 px-4 py-3 text-sm text-error-700 dark:border-error-500/30 dark:bg-error-500/10 dark:text-error-300">
          {loadError}
        </div>
      )}

      {!loadError && members.length === 0 ? (
        <div className="rounded-xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900 p-12 text-center">
          <p className="text-sm text-gray-500 dark:text-gray-400">Nenhum membro na equipe.</p>
        </div>
      ) : !loadError && (
        <div className="rounded-xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900 overflow-hidden">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-gray-100 dark:border-gray-800">
                <th className="px-5 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Nome</th>
                <th className="px-5 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Email</th>
                <th className="px-5 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Cargo</th>
                <th className="px-5 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">MFA</th>
                <th className="px-5 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Último acesso</th>
                <th className="px-5 py-3"></th>
              </tr>
            </thead>
            <tbody>
              {members.map((member) => (
                <tr key={member.id} className="border-b border-gray-50 last:border-0 dark:border-gray-800/50">
                  <td className="px-5 py-3 font-medium text-gray-900 dark:text-white">{member.name}</td>
                  <td className="px-5 py-3 text-gray-700 dark:text-gray-300">{member.email}</td>
                  <td className="px-5 py-3">
                    <span className="inline-flex rounded-full bg-gray-100 px-2 py-0.5 text-xs font-medium text-gray-700 dark:bg-gray-800 dark:text-gray-300">
                      {roleLabels[member.role] || member.role}
                    </span>
                  </td>
                  <td className="px-5 py-3">
                    <span className={`text-xs ${member.isTotpEnabled ? "text-success-600 dark:text-success-400" : "text-gray-400"}`}>
                      {member.isTotpEnabled ? "Ativo" : "Inativo"}
                    </span>
                  </td>
                  <td className="px-5 py-3 text-xs text-gray-500 dark:text-gray-400">
                    {member.lastLogin ? new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric" }).format(new Date(member.lastLogin)) : "Nunca"}
                  </td>
                  <td className="px-5 py-3">
                    {member.role !== "OWNER" && (
                      <DeleteButton onClick={() => askRemove(member.id, member.name)} ariaLabel={`Remover ${member.name} da equipe`} />
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {showForm && (
        <div className="fixed inset-0 z-[100000] flex items-center justify-center bg-gray-400/5 backdrop-blur-[3px] modal-backdrop-in">
          <div className="w-full max-w-md rounded-xl bg-white p-6 dark:bg-gray-900 shadow-xl modal-content-in">
            <h3 className="text-lg font-semibold text-gray-900 dark:text-white mb-4">Convidar Membro</h3>
            {formError && <div className="mb-4 p-3 rounded-lg bg-error-50 dark:bg-error-500/10 text-error-700 dark:text-error-400 text-sm">{formError}</div>}
            <form onSubmit={handleInvite} className="space-y-4">
              <Input
                label="Nome"
                type="text"
                value={formData.name}
                onChange={(e) => setFormData({ ...formData, name: e.target.value })}
                required
              />
              <Input
                label="Email"
                type="email"
                value={formData.email}
                onChange={(e) => setFormData({ ...formData, email: e.target.value })}
                autoComplete="email"
                required
              />
              <Input
                label="Senha inicial"
                type="password"
                value={formData.password}
                onChange={(e) => setFormData({ ...formData, password: e.target.value })}
                autoComplete="new-password"
                minLength={12}
                required
                hint="Mínimo 12 caracteres com maiúscula, minúscula, número e símbolo. O membro poderá trocar no primeiro acesso."
              />
              <div>
                <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Cargo</label>
                <Select
                  value={formData.role}
                  onChange={(v) => setFormData({ ...formData, role: v as UserRole })}
                  options={roleOptions}
                />
              </div>
              <div className="flex justify-end gap-3 pt-2">
                <button type="button" onClick={() => setShowForm(false)} className="rounded-lg px-4 py-2 text-sm font-medium text-gray-600 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800">Cancelar</button>
                <button type="submit" disabled={formLoading} className="rounded-lg bg-brand-500 px-4 py-2 text-sm font-medium text-white hover:bg-brand-600 disabled:opacity-50">{formLoading ? "Enviando..." : "Enviar Convite"}</button>
              </div>
            </form>
          </div>
        </div>
      )}

      <ConfirmModal
        isOpen={pending !== null}
        title="Remover membro"
        message={`Remover ${pending?.name} da equipe? O acesso ao portal será revogado imediatamente.`}
        confirmLabel="Remover"
        variant="danger"
        requireCode
        isLoading={pendingRunning}
        onCancel={() => { if (!pendingRunning) setPending(null); }}
        onConfirm={runRemove}
      />
    </div>
  );
}
