using Godot;

/// <summary>Additional strings used by score and track integration code.</summary>
public static class CommonIntegrationTranslations
{
	public static void Register()
	{
		var portuguese = new Translation { Locale = "pt_BR" };
		foreach (var (source, translated) in Entries) portuguese.AddMessage(source, translated);
		TranslationServer.AddTranslation(portuguese);
	}

	private static readonly (string Source, string Portuguese)[] Entries =
	{
		("Inserted {0} chord notes.", "{0} notas do acorde inseridas."),
		("The selected notes changed; reopen the expression editor.", "As notas selecionadas mudaram; abra o editor de expressões novamente."),
		("Select a named version first.", "Selecione primeiro uma versão com nome."),
		("Transition rule saved.", "Regra de transição salva."), ("Alternation order saved.", "Ordem de alternância salva."),
		("Double-click to rename this track.", "Clique duas vezes para renomear esta faixa."),
		("Mute this track", "Silenciar esta faixa"), ("Solo this track", "Reproduzir apenas esta faixa"),
		("Track volume in decibels.", "Volume da faixa em decibéis."), ("Delete track?", "Excluir faixa?"), ("Delete", "Excluir"),
		("Delete track {0} and its contents from all sections?", "Excluir a faixa {0} e seu conteúdo de todas as seções?"),
		("Linked repeat of source measure {0}. Edits update the source and its other repeats.", "Repetição vinculada ao compasso de origem {0}. As edições atualizam a origem e as outras repetições."),
		("Append source", "Adicionar origem"),
		("Append an empty independent measure. {0}+click to copy the previous measure with independent events.", "Adicionar um compasso independente vazio. {0}+clique copia o compasso anterior com eventos independentes."),
		("Measure {0} versions", "Versões do compasso {0}"),
		("Create versions, choose transition rules, and set fallback order.", "Crie versões, escolha regras de transição e defina a ordem alternativa."),
		("Remove", "Remover"), ("Select a named version first.", "Primeiro selecione uma versão com nome."),
		("Transition rule saved.", "Regra de transição salva."), ("Alternation order saved.", "Ordem de alternância salva."),
		("Repeats", "Repetições"),
		("Delete measure {0} from section {1}?\nThis removes its content from all tracks.", "Excluir o compasso {0} da seção {1}?\nO conteúdo será removido de todas as faixas."),
		("Cannot change tuning", "Não é possível alterar a afinação"),
		("Drag empty space to select. Shift-drag adds to selection.\n{0} copies notes; point at a destination and press {1} to paste.\nCtrl-click adds fret 0. Double-click edits a fret.\nCtrl+arrow keys edit or move notes.", "Arraste uma área vazia para selecionar. Shift+arraste adiciona à seleção.\n{0} copia notas; aponte para o destino e pressione {1} para colar.\nCtrl+clique adiciona a casa 0. Clique duas vezes para editar uma casa.\nCtrl+setas editam ou movem notas."),
		("Finish or cancel the current fret entry before copying.", "Conclua ou cancele a digitação da casa atual antes de copiar."),
		("Select one or more notes before copying.", "Selecione uma ou mais notas antes de copiar."),
		("The selection changed; select the notes again.", "A seleção mudou; selecione as notas novamente."),
		("Copied {0} note(s). Point at a destination and use the paste shortcut.", "{0} nota(s) copiada(s). Aponte para o destino e use o atalho de colagem."),
		("Point at a tablature measure before pasting.", "Aponte para um compasso da tablatura antes de colar."),
		("Finish or cancel the current fret entry before pasting.", "Conclua ou cancele a digitação da casa atual antes de colar."),
		("Copy one or more tablature notes first.", "Primeiro copie uma ou mais notas da tablatura."),
		("Point at a string and subdivision inside a measure to paste.", "Aponte para uma corda e subdivisão dentro de um compasso para colar."),
		("The pasted notes would overlap an attack from a linked repeat. Change the repeat count or paste position first.", "As notas coladas se sobreporiam a um ataque de uma repetição vinculada. Altere a quantidade de repetições ou a posição de colagem."),
		("Confirm paste", "Confirmar colagem"), ("Paste", "Colar"),
		("Paste {0} note(s) into {1}, measure {2}?\n\nExisting notes to replace: {3}\nCopied notes that cannot fit: {4}{5}", "Colar {0} nota(s) em {1}, compasso {2}?\n\nNotas existentes a substituir: {3}\nNotas copiadas que não cabem: {4}{5}"),
		("\n\nSome affected notes belong to linked measures: changes also appear in their repeated occurrences.", "\n\nAlgumas notas afetadas pertencem a compassos vinculados: as alterações também aparecem nas repetições."),
		("The destination changed. Select it and paste again.", "O destino mudou. Selecione-o e cole novamente."),
		("No copied notes fit at this position.", "Nenhuma das notas copiadas cabe nesta posição."),
		("Pasted {0} note(s).", "{0} nota(s) colada(s)."),
		("This track determines the focused section length.", "Esta faixa determina a duração da seção em foco."),
		("Make this track determine the focused section length.", "Definir esta faixa para determinar a duração da seção em foco."),
		("String {0}, fret {1}: {2}", "Corda {0}, casa {1}: {2}"),
	};
}
