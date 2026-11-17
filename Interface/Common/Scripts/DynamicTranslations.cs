#nullable enable
using Godot;

/// <summary>Translation entries used by editor controls whose text is generated at runtime.</summary>
public static class DynamicTranslations
{
	/// <summary>Registers dynamic editor strings. Call after AppTranslations.Register().</summary>
	public static void Register()
	{
		var english = new Translation { Locale = "en" };
		var portuguese = new Translation { Locale = "pt_BR" };
		foreach (var (source, translated) in Entries)
		{
			english.AddMessage(source, source);
			portuguese.AddMessage(source, translated);
		}
		TranslationServer.AddTranslation(english);
		TranslationServer.AddTranslation(portuguese);
	}

	private static readonly (string Source, string Portuguese)[] Entries =
	[
		("Could not load application settings: {0}", "Não foi possível carregar as configurações do aplicativo: {0}"),
		("Could not save application settings: {0}", "Não foi possível salvar as configurações do aplicativo: {0}"),
		("Choose a key other than Escape or a modifier.", "Escolha uma tecla diferente de Esc ou de uma tecla modificadora."),
		("Already assigned to: {0}", "Já atribuída a: {0}"),
		("String {0}. Drag to reorder.", "Corda {0}. Arraste para reordenar."),
		("Move string {0} up", "Mover a corda {0} para cima"), ("Move string {0} down", "Mover a corda {0} para baixo"),
		("Remove string {0}", "Remover a corda {0}"), ("String {0}", "Corda {0}"),
		("Drag to reorder. Drag the right edge to change repetitions.", "Arraste para reordenar. Arraste a borda direita para alterar as repetições."),
		("Curve underlines: green velocity, blue pitch, violet vibrato, orange tremolo. Matching shades identify linked curves.", "Sublinhados das curvas: velocidade verde, altura azul, vibrato violeta e tremolo laranja. Tons iguais identificam curvas vinculadas."),
		("Mixed", "Misto"), ("Original", "Original"),
		("Editing {0} notes", "Editando {0} notas"), ("Velocity {0} · {1} notes", "Velocidade {0} · {1} notas"),
		("Velocity {0}", "Velocidade {0}"),
		("Mixed across {0} notes · Apply shares this curve", "Misto entre {0} notas · Aplicar compartilha esta curva"),
		("No curve assigned · choose a preset or add points", "Nenhuma curva atribuída · escolha um predefinido ou adicione pontos"),
		("Shared curve · edits affect all linked notes", "Curva compartilhada · as edições afetam todas as notas vinculadas"),
		(" · Timing uses the first note's span", " · O tempo usa a duração da primeira nota"),
		("Pitch · semitones ({0} to {1}) · X = note progress", "Altura · semitons ({0} a {1}) · X = progresso da nota"),
		("Vibrato · semitone offset ({0} to {1}) · X = note progress", "Vibrato · variação em semitons ({0} a {1}) · X = progresso da nota"),
		("Tremolo · amplitude (0 to 1) · X = note progress", "Tremolo · amplitude (0 a 1) · X = progresso da nota"),
		("Velocity · multiplier (0 to 2) · X = selected attack span", "Velocidade · multiplicador (0 a 2) · X = duração do ataque selecionado"),
		("Choose…", "Escolha…"), ("Bend", "Bend"), ("Release", "Soltura"), ("Slide", "Deslizamento"),
		("Vibrato", "Vibrato"), ("Subtle vibrato", "Vibrato sutil"), ("Reset", "Redefinir"),
		("Tremolo", "Tremolo"), ("Deep tremolo", "Tremolo profundo"), ("Velocity rise", "Aumento de velocidade"), ("Velocity fall", "Redução de velocidade"),
		("Click a measure to set the playback start (green); orange shows playback.\nAlt-drag a note up or down to change velocity.\nDrag empty space to select notes; Shift-drag adds to selection.\nCtrl+Left/Right changes selected frets; Ctrl+Up/Down moves selected notes between strings.\n{0} copies; hover a destination and press {1}.\nCtrl-click adds an open-string note.\nDouble-click to edit a fret; Enter or an arrow confirms. Escape cancels.", "Clique em um compasso para definir o início da reprodução (verde); laranja indica a reprodução.\nAlt+arraste uma nota para cima ou para baixo para alterar a velocidade.\nArraste uma área vazia para selecionar notas; Shift+arraste adiciona à seleção.\nCtrl+Esquerda/Direita altera as casas selecionadas; Ctrl+Cima/Baixo move notas entre cordas.\n{0} copia; passe sobre o destino e pressione {1}.\nCtrl+clique adiciona uma nota de corda solta.\nClique duas vezes para editar uma casa; Enter ou uma seta confirma. Esc cancela."),
		("Click a measure to set the playback start.\nDouble-click empty space or Ctrl-click to add a hit.\nDrag hits to move or resize; Alt-drag up or down to change velocity.\nDrag empty space to select. Shift-drag adds to selection.\n{0} copies; hover a destination and press {1}.", "Clique em um compasso para definir o início da reprodução.\nClique duas vezes em uma área vazia ou use Ctrl+clique para adicionar uma batida.\nArraste batidas para mover ou redimensionar; Alt+arraste para cima ou para baixo altera a velocidade.\nArraste uma área vazia para selecionar. Shift+arraste adiciona à seleção.\n{0} copia; passe sobre o destino e pressione {1}."),
		("Fret: ", "Casa: "),
		("Open…", "Abrir…"), ("Save", "Salvar"), ("Save As…", "Salvar como…"), ("Open Recent", "Abrir recente"),
		("score default", "padrão da partitura"), ("section override", "substituição da seção"),
		("Lead for focused section {0} · {1}; it determines this section's length.", "Principal da seção em foco {0} · {1}; determina a duração desta seção."),
		("Make this track lead for focused section {0} · {1}.", "Definir esta faixa como principal da seção em foco {0} · {1}."),
		("Focused section {0} · {1}: {2}", "Seção em foco {0} · {1}: {2}"),
		("the lead track determines section length.", "a faixa principal determina a duração da seção."),
		("pattern length in measures; shorter patterns repeat.", "duração do padrão em compassos; padrões mais curtos se repetem."),
		("Incomplete", "Incompleto"), ("Overfull", "Excedente"),
		("Score default tempo in quarter-note BPM. Section defaults and measure tempo events override it at their positions.", "Andamento padrão da partitura em BPM de semínima. Padrões de seção e eventos de andamento dos compassos prevalecem nas respectivas posições."),
		("Play or pause from the selected measure.", "Reproduzir ou pausar a partir do compasso selecionado."),
		("Stop playback and return to the selected measure.", "Parar a reprodução e voltar ao compasso selecionado."),
		("Open score", "Abrir partitura"), ("File error", "Erro de arquivo"),
		("Discard unsaved changes?", "Descartar alterações não salvas?"),
		("The current score has unsaved changes. Continue and discard those changes?", "A partitura atual tem alterações não salvas. Continuar e descartá-las?"),
		("Discard and Continue", "Descartar e continuar"), ("No recent scores", "Nenhuma partitura recente"),
		("Master Vol.:", "Vol. mestre:"), ("Percussion master volume in decibels.", "Volume mestre da percussão em decibéis."),
		("Section {0} · {1}", "Seção {0} · {1}"), ("Section {0}: measure {1} time signature", "Seção {0}: fórmula de compasso {1}"),
		("Delete measure {0} from section {1}?\nThis removes its content from all tracks.", "Excluir o compasso {0} da seção {1}?\nIsso removerá seu conteúdo de todas as faixas."),
		("GM note {0}", "Nota GM {0}"), ("Set lead", "Definir como principal"),
		("Focused section {0} · {1}: ", "Seção em foco {0} · {1}: "),
		("Section {0}: repeats measure {1}; edits change the source pattern.", "Seção {0}: repete o compasso {1}; as edições alteram o padrão de origem."),
		("Add a beat in the remaining {0} whole notes. Normal click adds it silently; Ctrl-click copies the last beat's full state when it fits. Empty voices use the normal value.", "Adicione uma batida nas {0} semibreves restantes. Um clique normal a adiciona em silêncio; Ctrl+clique copia o estado completo da última batida quando couber. Vozes vazias usam o valor normal."),
		("The remaining duration cannot be represented by a supported beat.", "A duração restante não pode ser representada por uma batida compatível."),
		("Remove this beat.", "Remover esta batida."), ("Toggle a dotted note (adds half its base duration).", "Alternar nota pontuada (adiciona metade da duração base)."),
		("Toggle {0} at measure {1}, beat {2}.", "Alternar {0} no compasso {1}, batida {2}."),
		("Set execution intensity to {0} of 5 (MIDI velocity {1}).", "Definir intensidade de execução como {0} de 5 (velocidade MIDI {1})."),
		("Measure {0}, beat {1} rhythmic value.", "Valor rítmico do compasso {0}, batida {1}."),
		("Select a longer note value.", "Selecionar um valor de nota mais longo."), ("Select a shorter note value.", "Selecionar um valor de nota mais curto."),
	];
}
