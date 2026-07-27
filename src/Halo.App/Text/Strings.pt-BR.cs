using System.Collections.Generic;

namespace Halo.Text;

/// <summary>
/// Brazilian Portuguese. The pill is playful and lowercase in English, so the
/// translations keep the same voice rather than being literal.
/// </summary>
internal static class PtBr
{
    public static readonly IReadOnlyDictionary<string, string> Table = new Dictionary<string, string>
    {
        // ── agent activity ────────────────────────────────────────────────
        ["working…"] = "trabalhando…",
        ["writing…"] = "escrevendo…",
        ["reading…"] = "lendo…",
        ["running…"] = "executando…",
        ["digging…"] = "vasculhando…",
        ["fetching…"] = "buscando…",
        ["googling :P"] = "pesquisando :P",
        ["delegating…"] = "delegando…",
        ["planning…"] = "planejando…",
        ["using a skill…"] = "usando uma skill…",
        ["asking you :)"] = "te perguntando :)",
        ["peeking o.o"] = "espiando o.o",
        ["patching…"] = "corrigindo…",
        ["plotting…"] = "tramando…",
        ["compacting…"] = "compactando…",
        ["hmm…"] = "hmm…",

        // ── agent state ───────────────────────────────────────────────────
        ["idle"] = "ocioso",
        ["error"] = "erro",
        ["your move ;)"] = "tua vez ;)",
        ["let's work :)"] = "bora trabalhar :)",
        ["compacted :)"] = "compactado :)",
        ["outta juice :("] = "sem gás :(",
        ["outta juice XD"] = "sem gás XD",
        // Two spellings on purpose: lowercase is the pill text, capitalised is the panel heading.
        ["agent"] = "agente",
        ["Agent"] = "Agente",

        // ── connectivity ──────────────────────────────────────────────────
        ["offline :("] = "offline :(",
        ["api down :("] = "api fora :(",
        ["net error :("] = "erro de rede :(",
        ["api error :("] = "erro de api :(",
        ["your internet :("] = "tua internet :(",
        ["Anthropic's side :("] = "lado da Anthropic :(",
        ["OpenAI's side :("] = "lado da OpenAI :(",
        ["Bad internet :/"] = "Internet ruim :/",
        ["loss"] = "perda",
        ["net"] = "rede",

        // ── panels ────────────────────────────────────────────────────────
        ["No active Claude Code session"] = "Nenhuma sessão do Claude Code ativa",
        ["No active Codex session"] = "Nenhuma sessão do Codex ativa",
        ["Context"] = "Contexto",
        ["5-hour limit"] = "Limite de 5 horas",
        ["Weekly limit"] = "Limite semanal",
        ["Plan limit"] = "Limite do plano",
        ["usage never fetched"] = "uso nunca consultado",
        ["updated {0}"] = "atualizado {0}",
        ["refresh"] = "atualizar",

        // ── limit notices ─────────────────────────────────────────────────
        // The window name is interpolated into the sentence, so it is translated separately.
        ["{0} usage {1}%"] = "Uso do {0}: {1}%",
        ["You've used {0}% of your {1} limit."] = "Você usou {0}% do seu limite {1}.",
        ["5-hour"] = "de 5 horas",
        ["weekly"] = "semanal",
        ["primary"] = "primário",

        // ── downloads ─────────────────────────────────────────────────────
        ["Waiting…"] = "Aguardando…",
        ["Installing…"] = "Instalando…",
        ["Paused"] = "Pausado",
        ["Downloading"] = "Baixando",

        // ── time ──────────────────────────────────────────────────────────
        ["just now"] = "agora mesmo",
        ["now"] = "agora",
        ["{0}m ago"] = "há {0}min",
        ["{0}h ago"] = "há {0}h",
        ["{0}d ago"] = "há {0}d",
        ["back in {0}"] = "volta em {0}",
        ["resets {0}"] = "renova {0}",

        // ── credits ───────────────────────────────────────────────────────
        ["${0} left"] = "${0} restante",
        ["${0} left of ${1}"] = "${0} de ${1} restante",
        ["${0} used"] = "${0} usado",
        ["${0} credits"] = "${0} em créditos",

        // ── file tray ─────────────────────────────────────────────────────
        ["Drop files here"] = "Solte os arquivos aqui",
        ["Drop to add"] = "Solte para adicionar",
        ["Release to add"] = "Solte para adicionar",
        ["Image copied"] = "Imagem copiada",
        ["Screenshot captured"] = "Captura de tela feita",
        ["Empty"] = "Vazio",
        ["Remove {0}"] = "Remover {0}",
        ["File Tray"] = "Bandeja de arquivos",
        ["they'll stay in the tray"] = "eles ficam guardados na bandeja",

        // ── system notices ────────────────────────────────────────────────
        ["Battery low — {0}%"] = "Bateria fraca — {0}%",
        // The resource name is interpolated, so it is translated separately below.
        // "CPU" is deliberately absent: it is the same word in Portuguese and falls through.
        ["High {0} usage — {1}%"] = "Uso alto de {0} — {1}%",
        ["memory"] = "memória",
        ["Memory is running low."] = "A memória está acabando.",
        ["{0} is using the most."] = "{0} é quem mais está consumindo.",
        ["Tap to turn on Power Saver."] = "Toque para ligar a Economia de Energia.",

        // ── network notices ───────────────────────────────────────────────
        ["Wi-Fi disconnected"] = "Wi-Fi desconectado",
        ["Network disconnected"] = "Rede desconectada",
        ["Connected to {0}"] = "Conectado a {0}",
        ["Switched to {0}"] = "Trocou para {0}",
        ["Was on {0}"] = "Estava em {0}",
        ["No internet"] = "Sem internet",
        ["this network"] = "esta rede",
        ["{0} needs you to sign in"] = "{0} exige login",
        ["Connected, but not online yet"] = "Conectado, mas ainda sem internet",
        ["Store app"] = "App da Store",
        ["Xbox game"] = "Jogo do Xbox",

        // ── notification source names ─────────────────────────────────────
        ["System"] = "Sistema",
        ["Battery"] = "Bateria",
        ["Network"] = "Rede",
        ["Screenshot"] = "Captura de tela",
        ["Clipboard"] = "Área de transferência",
        ["Clock"] = "Relógio",
        ["Keyboard"] = "Teclado",

        // ── notification banner ───────────────────────────────────────────
        ["Copied"] = "Copiado",

        // ── bluetooth ─────────────────────────────────────────────────────
        ["Bluetooth device"] = "Dispositivo Bluetooth",
        ["{0}% battery"] = "{0}% de bateria",
    };
}
