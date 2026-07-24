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
        ["agent"] = "agente",

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
        ["High memory usage — {0}%"] = "Uso alto de memória — {0}%",
        ["Memory is running low."] = "A memória está acabando.",
        ["{0} is using the most."] = "{0} é quem mais está consumindo.",
        ["Tap to turn on Power Saver."] = "Toque para ligar a Economia de Energia.",
        ["Store app"] = "App da Store",
        ["Xbox game"] = "Jogo do Xbox",

        // ── notification source names ─────────────────────────────────────
        ["System"] = "Sistema",
        ["Battery"] = "Bateria",
        ["Network"] = "Rede",
        ["Screenshot"] = "Captura de tela",

        // ── bluetooth ─────────────────────────────────────────────────────
        ["Bluetooth device"] = "Dispositivo Bluetooth",
        ["{0}% battery"] = "{0}% de bateria",
    };
}
