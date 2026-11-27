#nullable enable
using Godot;

/// <summary>Central runtime catalog for the application's built-in UI strings.</summary>
public static class AppTranslations
{
	private static bool _registered;
	/// <summary>Registers the source English catalog and Brazilian Portuguese translations.</summary>
	public static void Register()
	{
		if (_registered) return;
		_registered = true;
		var english = new Translation { Locale = "en" };
		var portuguese = new Translation { Locale = "pt_BR" };
		foreach (var (source, translated) in Entries)
		{
			english.AddMessage(source, source);
			portuguese.AddMessage(source, translated);
		}
		foreach (var (source, translated) in SceneTranslations.Entries)
		{
			english.AddMessage(source, source);
			portuguese.AddMessage(source, translated);
		}
		ThemeTranslations.Register(english, portuguese);
		TranslationServer.AddTranslation(english);
		TranslationServer.AddTranslation(portuguese);
		DynamicTranslations.Register();
		CommonIntegrationTranslations.Register();
		AudioTranslations.Register();
	}

	// Keys intentionally match the UI's source literals so callers can use Tr("...") directly.
	private static readonly (string Source, string Portuguese)[] Entries =
	{
		("Settings", "Configurações"), ("Preferences…", "Preferências…"), ("File", "Arquivo"), ("Edit", "Editar"),
		("Language", "Idioma"), ("Shortcuts", "Atalhos"), ("Interface language", "Idioma da interface"),
		("Language changes apply immediately.", "As alterações de idioma são aplicadas imediatamente."),
		("English", "Inglês"), ("Português (Brasil)", "Português (Brasil)"), ("Reset shortcuts", "Restaurar atalhos"),
		("Select an action, then press one key.", "Selecione uma ação e pressione uma tecla."),
		("Press a key for this shortcut. Escape cancels.", "Pressione uma tecla para este atalho. Esc cancela."),
		("Press a key…", "Pressione uma tecla…"), ("Shortcut saved.", "Atalho salvo."),
		("Shortcut change cancelled.", "Alteração do atalho cancelada."), ("Default shortcuts restored.", "Atalhos padrão restaurados."),
		("That key is already assigned.", "Esta tecla já está atribuída."), ("Unassigned", "Não atribuído"),
		("New score", "Nova partitura"), ("Save score", "Salvar partitura"), ("Undo", "Desfazer"), ("Redo", "Refazer"),
		("Copy notes", "Copiar notas"), ("Paste notes", "Colar notas"), ("Play / stop", "Reproduzir / parar"), ("Play from start", "Reproduzir desde o início"),
		("BPM", "BPM"), ("Tap", "Marcar"), ("Tap four times to set the score tempo.", "Toque quatro vezes para definir o andamento da partitura."),
		("Toggle the playback metronome.", "Ativar ou desativar o metrônomo da reprodução."),
		("Add a count-in before playback.", "Adicionar uma contagem antes da reprodução."),
		("Repeat the count-in at each loop.", "Repetir a contagem a cada repetição."), ("+ Add Track", "+ Adicionar faixa"),
		("Sections", "Seções"), ("Add a section", "Adicionar uma seção"), ("Arrangement", "Arranjo"), ("Select", "Selecionar"),
		("New", "Novo"), ("Arrange name", "Nome do arranjo"), ("Shortcuts: Off", "Atalhos: desativados"), ("Shortcuts: On", "Atalhos: ativados"),
		("Route playback shortcuts to the arrangement player.", "Direcionar os atalhos de reprodução para o reprodutor de arranjos."),
		("Route playback shortcuts to the arrangement player. While an arrangement plays, Play / stop stops all players.", "Direcionar os atalhos de reprodução para o reprodutor de arranjos. Durante a reprodução de um arranjo, Reproduzir / parar interrompe todos os reprodutores."),
		("Route playback shortcuts to the arrangement player. While an arrangement plays, {0} stops all players.", "Direcionar os atalhos de reprodução para o reprodutor de arranjos. Durante a reprodução de um arranjo, {0} interrompe todos os reprodutores."),
		("Play or pause from the selected measure. {0} starts or stops playback.", "Reproduzir ou pausar a partir do compasso selecionado. {0} inicia ou interrompe a reprodução."),
		("Play the arrangement from the beginning.", "Reproduzir o arranjo desde o início."), ("Stop arrangement playback.", "Parar a reprodução do arranjo."),
		("Repeat the arrangement.", "Repetir o arranjo."), ("Delete this arrangement.", "Excluir este arranjo."),
		("Playback {0}% across {1} repetitions. Drag the body to reorder; drag the right edge to change repetitions.", "Reprodução {0}% ao longo de {1} repetições. Arraste o centro para reordenar; arraste a borda direita para alterar as repetições."),
		("The editor is locked while the arrangement plays. Stop playback to edit.", "O editor fica bloqueado durante a reprodução do arranjo. Pare a reprodução para editar."),
		("INSTRUMENT", "INSTRUMENTO"), ("Choose a track family and instrument", "Escolha uma família de faixas e um instrumento"),
		("General MIDI program", "Programa General MIDI"), ("TRACK", "FAIXA"), ("Track name", "Nome da faixa"),
		("Track color", "Cor da faixa"), ("Choose a track color", "Escolher a cor da faixa"), ("TUNING", "AFINAÇÃO"),
		("String 1 is shown first. Pitches use MIDI notes 0–127.", "A corda 1 é exibida primeiro. As notas usam valores MIDI de 0 a 127."),
		("Strings", "Cordas"), ("Add high string", "Adicionar corda aguda"), ("Add a string five semitones above string 1", "Adicionar uma corda cinco semitons acima da corda 1"),
		("Initialize tuning", "Inicializar afinação"), ("Add low string", "Adicionar corda grave"), ("Add a string seven semitones below the last string", "Adicionar uma corda sete semitons abaixo da última corda"),
		("When applying this tuning change", "Ao aplicar esta alteração de afinação"), ("IMAGE", "IMAGEM"), ("Apply", "Aplicar"),
		("Raise by one semitone", "Subir um semitom"), ("Lower by one semitone", "Descer um semitom"),
		("Enter a note name, such as C#4 or E3", "Digite o nome de uma nota, como C#4 ou E3"),
		("Invalid pitch. Use A–G, an optional sharp, and an octave from -1 to 9 (MIDI 0–127).", "Nota inválida. Use A–G, sustenido opcional e oitava de -1 a 9 (MIDI 0–127)."),
		("No tuning is configured for this track. Choose Initialize tuning to set one.", "Esta faixa não tem afinação configurada. Escolha Inicializar afinação para defini-la."),
		("Retuning options have not been checked yet.", "As opções de re-afinação ainda não foram verificadas."),
		("Fix each invalid pitch before changing string order or applying.", "Corrija cada nota inválida antes de alterar a ordem das cordas ou aplicar."),
		("Click the desired time in this track's tablature to insert the chord. Escape cancels placement.", "Clique no momento desejado na tablatura desta faixa para inserir o acorde. Esc cancela a inserção."),
		("Open keyboard", "Abrir teclado"), ("Start octave", "Oitava inicial"), ("Octaves", "Oitavas"),
		("Selected: {0}", "Selecionadas: {0}"),
		("Enable note editing in this measure before inserting a chord.", "Ative a edição de notas neste compasso antes de inserir um acorde."),
		("The entire chord must fit on the track's note lanes.", "O acorde inteiro precisa caber nas linhas de notas da faixa."),
		("Fretboard is available for configured string instruments.", "O braço está disponível para instrumentos de cordas configurados."), ("Open fretboard", "Abrir braço"),
		("Chord", "Acorde"), ("Create Mode", "Modo de criação"), ("Insert Chord", "Inserir acorde"),
		("Finish or cancel the current fret entry before inserting a chord.", "Conclua ou cancele a digitação da casa atual antes de inserir um acorde."),
		("Click a position inside this track's tablature to insert the chord.", "Clique em uma posição na tablatura desta faixa para inserir o acorde."),
		("Confirm chord insertion", "Confirmar inserção do acorde"), ("Insert", "Inserir"),
		("Snap", "Ajuste"), ("Snap note positions and durations for this track", "Ajustar posições e durações das notas desta faixa"),
		("Editing snap for this measure and track.", "Ajuste de edição deste compasso e faixa."),
		("Default snap for newly created measures on this track.", "Ajuste padrão para novos compassos desta faixa."),
		("Tab pages", "Páginas da tablatura"), ("Previous tab page", "Página anterior da tablatura"), ("Next tab page", "Próxima página da tablatura"),
		("Scroll to the previous measures in the selected sections.", "Ir para os compassos anteriores nas seções selecionadas."), ("Scroll to the next measures in the selected sections.", "Ir para os próximos compassos nas seções selecionadas."),
		("Measures {0}–{1}", "Compassos {0}–{1}"),
		("Measure {0}", "Compasso {0}"), ("{0} · {1}", "{0} · {1}"),
		("Delete this track", "Excluir esta faixa"), ("Add a percussion piece", "Adicionar uma peça de percussão"), ("+ Piece", "+ Peça"),
		("+ Measure", "+ Compasso"), ("Lead", "Principal"), ("Set lead", "Definir como principal"),
		("This track determines the section length.", "Esta faixa determina a duração da seção."), ("Make this track determine the section length.", "Definir esta faixa para determinar a duração da seção."),
		("Mute this track", "Silenciar esta faixa"), ("Solo this track", "Reproduzir apenas esta faixa"), ("Track volume in decibels.", "Volume da faixa em decibéis."),
		("Double-click to rename this track.", "Clique duas vezes para renomear esta faixa."),
		("Double-click to rename this kit.", "Clique duas vezes para renomear este kit."), ("Delete percussion kit?", "Excluir kit de percussão?"),
		("Delete this kit and its contents from all sections?", "Excluir este kit e seu conteúdo de todas as seções?"),
		("Percussion piece", "Peça de percussão"), ("Delete percussion piece?", "Excluir peça de percussão?"),
		("The percussion piece could not be removed.", "Não foi possível remover a peça de percussão."),
		("Delete section", "Excluir seção"), ("Delete this section and its contents from all tracks?", "Excluir esta seção e seu conteúdo de todas as faixas?"),
		("Delete track?", "Excluir faixa?"), ("Delete", "Excluir"), ("Paste", "Colar"),
		("Finish or cancel the current fret entry before copying.", "Conclua ou cancele a digitação da casa atual antes de copiar."),
		("Point at a tablature measure before pasting.", "Aponte para um compasso da tablatura antes de colar."),
		("Finish or cancel the current fret entry before pasting.", "Conclua ou cancele a digitação da casa atual antes de colar."),
		("Copy one or more tablature notes first.", "Primeiro copie uma ou mais notas da tablatura."),
		("Point at a string and subdivision inside a measure to paste.", "Aponte para uma corda e subdivisão dentro de um compasso para colar."),
		("No copied notes fit at this position.", "Nenhuma das notas copiadas cabe nesta posição."),
		("TAB help · hover for shortcuts", "Ajuda da TAB · passe o cursor para ver os atalhos"),
		("Double-click to rename this track.", "Clique duas vezes para renomear esta faixa."),
		("Mute this drum track", "Silenciar esta faixa de bateria"), ("Solo this drum track", "Reproduzir apenas esta faixa de bateria"),
		("Set this track as the section lead", "Definir esta faixa como principal da seção"),
		("Number of measures in this track's repeating section pattern", "Número de compassos no padrão de repetição desta faixa na seção"),
		("Delete this measure", "Excluir este compasso"), ("Insert a measure after this one", "Inserir um compasso após este"),
		("Complete", "Completo"), ("Delete this percussion piece", "Excluir esta peça de percussão"),
		("Percussion piece settings", "Configurações da peça de percussão"), ("Mute this percussion piece", "Silenciar esta peça de percussão"),
		("Solo this percussion piece", "Reproduzir apenas esta peça de percussão"), ("Percussion piece volume", "Volume da peça de percussão"),
		("Repeats", "Repetições"), ("Additional repeats for the selected measure.", "Repetições adicionais do compasso selecionado."),
		("Tablature", "Tablatura"), ("Partiture", "Partitura"),
		("Repeat the full score until stopped.", "Repetir a partitura inteira até parar."),
		("Repeat the selected measure until stopped.", "Repetir o compasso selecionado até parar."),
		("Repeat the selected sections in score order until stopped.", "Repetir as seções selecionadas na ordem da partitura até parar."),
		("Drag to reorder. Drag the right edge to change repetitions.", "Arraste para reordenar. Arraste a borda direita para alterar as repetições."),
		("Drag the body to reorder. Drag the right edge to change repetitions.", "Arraste o centro para reordenar. Arraste a borda direita para alterar as repetições."),
		("Preview", "Prévia"), ("Stop", "Parar"), ("Left-click to add/drag · right-click a point to remove", "Clique esquerdo para adicionar/arrastar · clique direito em um ponto para removê-lo"),
		("Minimum must be below maximum.", "O mínimo deve ser menor que o máximo."), ("Range cannot exclude existing curve points.", "O intervalo não pode excluir pontos existentes da curva."),
		("Edit curves…", "Editar curvas…"), ("Note effects", "Efeitos da nota"), ("Sound", "Som"),
		("Incoming", "Entrada"), ("Clear", "Limpar"),
		("Select two consecutive sustained notes on the same string with the correct pitch direction.", "Selecione duas notas consecutivas e sustentadas na mesma corda, com a direção correta de altura."),
		("Hammer on from the previous note on this string", "Ligado ascendente a partir da nota anterior nesta corda"),
		("Pull off from the previous note on this string", "Ligado descendente a partir da nota anterior nesta corda"),
		("Clear the incoming articulation", "Limpar a articulação de entrada"),
		("Preview connected pair", "Pré-visualizar par ligado"),
		("Select one authored hammer-on or pull-off note to preview its connected pair.", "Selecione uma nota com ligado ascendente ou descendente para pré-visualizar o par conectado."),
		("The selected note has no previewable incoming articulation.", "A nota selecionada não tem uma articulação de entrada que possa ser pré-visualizada."),
		("The selected note changed before the pair could be previewed.", "A nota selecionada mudou antes que o par pudesse ser pré-visualizado."),
		("The selected note has no destination occurrence.", "A nota selecionada não tem ocorrência de destino."),
		("The immediately preceding note is not sustained through the connected note.", "A nota imediatamente anterior não é sustentada até a nota ligada."),
		("The selected note has different predecessors across repeats or sections.", "A nota selecionada tem predecessoras diferentes entre repetições ou seções."),
		("The connected note has different timing across repeats or sections.", "A nota ligada tem tempos diferentes entre repetições ou seções."),
		("The pitch direction does not match the authored articulation.", "A direção da altura não corresponde à articulação registrada."),
		("Connected string-note preview requires a configured string track.", "A pré-visualização de notas ligadas exige uma faixa de cordas afinada."),
		("The destination note has no authored hammer-on or pull-off.", "A nota de destino não tem ligado ascendente ou descendente registrado."),
		("The selected notes do not form a valid connected pair.", "As notas selecionadas não formam um par ligado válido."),
		("Incoming articulations require selected notes on a configured string track.", "Articulações de entrada exigem notas selecionadas em uma faixa de cordas afinada."),
		("The selected notes changed before the articulation could be applied.", "As notas selecionadas mudaram antes que a articulação pudesse ser aplicada."),
		("A selected note has no visible destination occurrence.", "Uma nota selecionada não tem ocorrência visível no destino."),
		("Each selected note needs a sustained immediately preceding note on the same string.", "Cada nota selecionada precisa de uma nota anterior sustentada na mesma corda."),
		("This selection has different preceding notes across repeats or sections; apply the articulation to a smaller selection.", "Esta seleção tem notas anteriores diferentes entre repetições ou seções; aplique a articulação a uma seleção menor."),
		("A hammer-on must move to a higher pitch on the same string.", "Um ligado ascendente deve ir para uma nota mais aguda na mesma corda."),
		("A pull-off must move to a lower pitch on the same string.", "Um ligado descendente deve ir para uma nota mais grave na mesma corda."),
		("Some variations use a different instrument preset, so their tone may change.", "Algumas variações usam outro timbre de instrumento e podem alterar o som."),
		("Delete an arrangement?", "Excluir um arranjo?"),
		("BeachLine Theme Preview", "Prévia do tema BeachLine"), ("Shared theme controls and variations from Interface/Themes/main.tres", "Controles e variações do tema compartilhado de Interface/Themes/main.tres"),
		("Button states", "Estados dos botões"), ("Arrangement and repeated-beat indicators", "Indicadores de arranjo e batidas repetidas"),
		("Sample text", "Texto de exemplo"), ("Placeholder", "Texto de exemplo"),
	};
}


