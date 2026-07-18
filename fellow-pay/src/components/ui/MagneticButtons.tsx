"use client";
import { useEffect } from "react";

/**
 * Efeito magnético global em TODOS os botões primários (`bg-brand-500`) do app.
 * Inspirado em phucbm/magnetic-button: o botão é "atraído" pelo cursor mesmo
 * quando ele está PERTO (fora das bordas) — não só quando está em cima.
 *
 * Diferença vs hover comum: padrão `mouseenter/mouseleave` só dispara quando
 * o cursor entra/sai dos bounds. Aqui rastreamos a posição do mouse
 * globalmente (throttle por requestAnimationFrame) e calculamos a distância
 * pra cada botão visível. Se o cursor está dentro de um raio expandido
 * (button bounds + `MAGNETIC_RADIUS_PX` de padding), o botão pulled.
 *
 * Performance:
 *  - IntersectionObserver mantém um Set de botões visíveis na viewport.
 *    Botões off-screen são ignorados.
 *  - MutationObserver detecta botões adicionados/removidos do DOM (navegação,
 *    modais, etc.) e mantém o Set sincronizado.
 *  - rAF throttle no mousemove — máximo 1 cálculo por frame.
 *
 * Skip em pointer:coarse (touch devices) — `hover` não existe lá.
 */
const MAGNETIC_SELECTOR = [
  // Botões primários brand (Solicitar Saque, Entrar, Confirmar, etc.)
  "button.bg-brand-500:not(.no-magnet):not(.no-pill):not(:disabled)",
  'a.bg-brand-500:not(.no-magnet):not(.no-pill):not([aria-disabled="true"])',
  // Opt-in explícito via `.btn-magnetic` — usado por botões neutros que
  // querem o efeito mas não são bg-brand-500 (ex: "Entrar com Google").
  "button.btn-magnetic:not(.no-magnet):not(:disabled)",
  'a.btn-magnetic:not(.no-magnet):not([aria-disabled="true"])',
].join(", ");

// Quantos pixels fora dos bounds do botão ainda contam como "campo magnético".
const MAGNETIC_RADIUS_PX = 15;

// Multiplicador da força. 0.015 = micro-deslocamento mínimo (1.5% do offset)
// — efeito quase fantasma, só notado em movimentos lentos do mouse perto
// do botão.
const MAGNETIC_STRENGTH = 0.015;

export function MagneticButtons() {
  useEffect(() => {
    if (typeof window === "undefined") return;
    const isFinePointer = window.matchMedia("(pointer: fine)").matches;
    if (!isFinePointer) return;

    // Set de botões atualmente visíveis (na viewport). Evita iterar elementos
    // off-screen no mousemove handler.
    const visibleButtons = new Set<HTMLElement>();

    const io = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          const el = entry.target as HTMLElement;
          if (entry.isIntersecting) visibleButtons.add(el);
          else {
            visibleButtons.delete(el);
            // Reset translate pra não ficar travado off-screen.
            el.style.translate = "0px 0px";
          }
        }
      },
      // rootMargin negativo NÃO — queremos um pouco extra pra detectar
      // botões que estão entrando na viewport com seu campo magnético junto.
      { rootMargin: `${MAGNETIC_RADIUS_PX}px` }
    );

    // Registra todos os botões matching no DOM atual.
    const observeAll = () => {
      document.querySelectorAll<HTMLElement>(MAGNETIC_SELECTOR).forEach((btn) => {
        io.observe(btn);
      });
    };
    observeAll();

    // Re-scan quando o DOM muda (page navigation, modals, dropdowns, etc.).
    const mo = new MutationObserver(() => {
      observeAll();
    });
    mo.observe(document.body, { childList: true, subtree: true });

    let mouseX = -9999;
    let mouseY = -9999;
    let rafId: number | null = null;

    const update = () => {
      rafId = null;
      visibleButtons.forEach((btn) => {
        const rect = btn.getBoundingClientRect();
        // Distância do cursor ao ponto mais próximo do rect (não ao centro
        // — assim o efeito ativa simétrico em volta do botão).
        const closestX = Math.max(rect.left, Math.min(mouseX, rect.right));
        const closestY = Math.max(rect.top, Math.min(mouseY, rect.bottom));
        const dx = mouseX - closestX;
        const dy = mouseY - closestY;
        const distance = Math.hypot(dx, dy);

        if (distance > MAGNETIC_RADIUS_PX) {
          // Fora do campo — reset (CSS transition faz o retorno elástico).
          // Usa `translate` (propriedade CSS standalone) ao invés de
          // `transform` pra NÃO conflitar com utilities Tailwind tipo
          // `active:scale-*` que setam transform na mesma cascade.
          btn.style.translate = "0px 0px";
          return;
        }

        // Dentro do campo — força atração com falloff baseado na distância.
        // Quanto mais perto, mais forte; chega ao máximo quando cursor está
        // dentro do botão (distance = 0).
        const falloff = 1 - distance / MAGNETIC_RADIUS_PX;
        const cx = rect.left + rect.width / 2;
        const cy = rect.top + rect.height / 2;
        const pullX = (mouseX - cx) * MAGNETIC_STRENGTH * falloff;
        const pullY = (mouseY - cy) * MAGNETIC_STRENGTH * falloff;
        btn.style.translate = `${pullX}px ${pullY}px`;
      });
    };

    const onMouseMove = (e: MouseEvent) => {
      mouseX = e.clientX;
      mouseY = e.clientY;
      // rAF throttle — no máximo 1 update por frame, mesmo se mousemove
      // dispara 60-1000Hz.
      if (rafId === null) rafId = requestAnimationFrame(update);
    };

    const onMouseLeave = () => {
      // Cursor saiu da window — zera todos os pulls.
      visibleButtons.forEach((btn) => {
        btn.style.translate = "0px 0px";
      });
    };

    window.addEventListener("mousemove", onMouseMove, { passive: true });
    document.addEventListener("mouseleave", onMouseLeave);

    return () => {
      window.removeEventListener("mousemove", onMouseMove);
      document.removeEventListener("mouseleave", onMouseLeave);
      io.disconnect();
      mo.disconnect();
      if (rafId !== null) cancelAnimationFrame(rafId);
    };
  }, []);

  return null;
}
