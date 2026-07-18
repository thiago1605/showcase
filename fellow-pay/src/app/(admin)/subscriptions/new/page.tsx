"use client";
import React, { useState } from "react";
import { useRouter } from "next/navigation";
import { subscriptionsService } from "@/services/subscriptions.service";
import { Select } from "@/components/ui/Select";
import { BackLink } from "@/components/ui/BackLink";
import { PageHeader } from "@/components/ui/PageHeader";
import Input from "@/components/form/input/InputField";

export default function NewSubscriptionPage() {
  const router = useRouter();
  const [formData, setFormData] = useState({ customerId: "", amount: "", description: "", interval: "MONTHLY", maxCycles: "" });
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState("");

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError("");
    setIsLoading(true);
    try {
      await subscriptionsService.create({
        customerId: formData.customerId,
        amount: parseFloat(formData.amount),
        description: formData.description,
        interval: formData.interval,
        maxCycles: formData.maxCycles ? parseInt(formData.maxCycles) : undefined,
      });
      router.push("/subscriptions");
    } catch {
      setError("Erro ao criar assinatura. Verifique os dados.");
    }
    setIsLoading(false);
  };

  return (
    <div className="space-y-6 max-w-lg">
      <BackLink fallbackHref="/subscriptions" />

      <PageHeader
        title="Nova assinatura"
        subtitle="Crie uma cobrança recorrente para um cliente."
      />

      {error && <div className="p-3 rounded-lg bg-error-50 dark:bg-error-500/10 text-error-700 dark:text-error-400 text-sm">{error}</div>}

      <form onSubmit={handleSubmit} className="space-y-4 rounded-xl border border-gray-200 bg-white p-5 dark:border-gray-800 dark:bg-gray-900">
        <Input
          label="ID do Cliente"
          type="text"
          value={formData.customerId}
          onChange={(e) => setFormData({ ...formData, customerId: e.target.value })}
          placeholder="ID do cliente"
          required
        />
        <Input
          label="Descrição"
          type="text"
          value={formData.description}
          onChange={(e) => setFormData({ ...formData, description: e.target.value })}
          placeholder="Ex: Plano mensal premium"
          required
        />
        <Input
          label="Valor (R$)"
          type="number"
          step={0.01}
          min="0.01"
          value={formData.amount}
          onChange={(e) => setFormData({ ...formData, amount: e.target.value })}
          required
        />
        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Intervalo</label>
            <Select
              value={formData.interval}
              onChange={(v) => setFormData({ ...formData, interval: v })}
              options={[
                { value: "WEEKLY", label: "Semanal" },
                { value: "MONTHLY", label: "Mensal" },
                { value: "QUARTERLY", label: "Trimestral" },
                { value: "YEARLY", label: "Anual" },
              ]}
            />
          </div>
          <Input
            label="Máx. ciclos"
            type="number"
            min="1"
            value={formData.maxCycles}
            onChange={(e) => setFormData({ ...formData, maxCycles: e.target.value })}
            placeholder="Ilimitado"
          />
        </div>
        <div className="flex justify-end gap-3 pt-2">
          <button type="button" onClick={() => router.back()} className="rounded-lg px-4 py-2 text-sm font-medium text-gray-600 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800">Cancelar</button>
          <button type="submit" disabled={isLoading} className="rounded-lg bg-brand-500 px-4 py-2.5 text-sm font-medium text-white hover:bg-brand-600 disabled:opacity-50">{isLoading ? "Criando..." : "Criar Assinatura"}</button>
        </div>
      </form>
    </div>
  );
}
