#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Godot;

/// <summary>Portuguese strings used by audio-track UI generated at runtime.</summary>
public static class AudioTranslations
{
	private static readonly IReadOnlyDictionary<string, string> Portuguese = new Dictionary<string, string>(StringComparer.Ordinal)
	{
		["+ Audio Track"] = "+ Faixa de áudio", ["Add a track for imported WAV audio."] = "Adicionar uma faixa para áudio WAV importado.", ["Import PCM WAV"] = "Importar WAV PCM",
		["WAV clips play at fixed speed."] = "Clipes WAV são reproduzidos em velocidade fixa.",
		["This audio track is no longer available."] = "Esta faixa de áudio não está mais disponível.",
		["Create or select an arrangement before importing arrangement audio."] = "Crie ou selecione um arranjo antes de importar áudio para o arranjo.",
		["The project changed before the WAV import completed. Reopen Import WAV on the intended track."] = "O projeto mudou antes da conclusão da importação do WAV. Abra Importar WAV novamente na faixa desejada.",
		["Imported {0}."] = "{0} importado.", ["WAV import failed: {0}"] = "Falha ao importar WAV: {0}",
		["Add an audio track and select it before recording."] = "Adicione uma faixa de áudio e selecione-a antes de gravar.",
		["The target section is no longer available."] = "A seção de destino não está mais disponível.",
		["Recording onto a track with existing clips is not supported yet. Choose an empty track."] = "A gravação em uma faixa que já contém clipes ainda não é compatível. Escolha uma faixa vazia.",
		["The recording offset must be shorter than the selected section or arrangement. Add a measure or reduce the offset."] = "O deslocamento da gravação deve ser menor que a duração da seção ou do arranjo selecionado. Adicione um compasso ou reduza o deslocamento.",
		["The project changed while recording was starting. No take was started."] = "O projeto mudou durante a preparação da gravação. Nenhuma tomada foi iniciada.",
		["The recording destination changed before transport became ready."] = "O destino da gravação mudou antes de o transporte ficar pronto.",
		["Recording paused. Press Space to resume the same take."] = "Gravação pausada. Pressione Espaço para continuar a mesma tomada.",
		["Recording to {0}. Use Stop Recording to finish, or Cancel Recording to discard the take."] = "Gravando em {0}. Use Parar gravação para concluir ou Cancelar gravação para descartar a tomada.",
		["Could not start recording: {0}"] = "Não foi possível iniciar a gravação: {0}",
		["Add a section to the arrangement before recording against its transport."] = "Adicione uma seção ao arranjo antes de gravar usando o transporte dele.",
		["Arrangement playback could not start, so recording was not started."] = "Não foi possível iniciar a reprodução do arranjo; a gravação não foi iniciada.",
		["Arrangement transport did not become ready, so recording was not started."] = "O transporte do arranjo não ficou pronto; a gravação não foi iniciada.",
		["Add a measure to the selected section before recording against its transport."] = "Adicione um compasso à seção selecionada antes de gravar usando o transporte dela.",
		["Section playback could not start, so recording was not started."] = "Não foi possível iniciar a reprodução da seção; a gravação não foi iniciada.",
		["Section transport did not become ready, so recording was not started."] = "O transporte da seção não ficou pronto; a gravação não foi iniciada.",
		["Transport stopped before recording could start."] = "O transporte parou antes que a gravação pudesse começar.",
		["Count-in before recording. Press Space to pause or Cancel Recording to stop."] = "Contagem antes da gravação. Pressione Espaço para pausar ou Cancelar gravação para parar.",
		["Transport could not start: {0}"] = "Não foi possível iniciar o transporte: {0}",
		["Recording could not be finalized; the partial take was retained."] = "Não foi possível finalizar a gravação; a tomada parcial foi mantida.",
		["Recording saved as {0}."] = "Gravação salva como {0}.",
		["Take completed but could not be added: {0}. The take remains in the audio cache."] = "A tomada foi concluída, mas não pôde ser adicionada: {0}. Ela permanece no cache de áudio.",
		["Recording start canceled."] = "Início da gravação cancelado.", ["Recording canceled."] = "Gravação cancelada.",
		["Stop or cancel this track’s recording before deleting the track."] = "Pare ou cancele a gravação desta faixa antes de excluí-la.",
		["Track color"] = "Cor da faixa", ["Double-click to rename this track."] = "Clique duas vezes para renomear esta faixa.",
		["Delete track?"] = "Excluir faixa?", ["Delete"] = "Excluir",
		["Delete audio track ‘{0}’ and its clips from all sections and arrangements?"] = "Excluir a faixa de áudio ‘{0}’ e seus clipes de todas as seções e arranjos?",
		["Default"] = "Padrão", ["Mono"] = "Mono", ["Stereo"] = "Estéreo", ["Section"] = "Seção", ["Arrangement"] = "Arranjo",
		["Missing WAV"] = "WAV ausente", ["Waveform unavailable: source WAV is missing."] = "Forma de onda indisponível: o WAV de origem está ausente.",
		["The clip cannot be moved there; keep it inside the timeline and clear of other clips."] = "O clipe não pode ser movido para lá; mantenha-o dentro da linha do tempo e sem sobreposição com outros clipes.",
		["Arrangement · {0}"] = "Arranjo · {0}", ["{0} clips ({1})"] = "{0} clipes ({1})",
		["Section ruler · {0} · {1} measures"] = "Régua da seção · {0} · {1} compassos",
		["Section ruler · {0} · {1}"] = "Régua da seção · {0} · {1}",
		["Audio playback warning: {0}"] = "Aviso de reprodução de áudio: {0}",
		["Audio playback underrun: {0} frames were dropped. Playback timing may be affected."] = "Subfluxo na reprodução de áudio: {0} quadros foram descartados. O tempo da reprodução pode ser afetado.",
		["Recording failed: {0}. The partial take was retained for recovery."] = "Falha na gravação: {0}. A tomada parcial foi mantida para recuperação.",
		["Could not add audio track: {0}"] = "Não foi possível adicionar a faixa de áudio: {0}",
		["This project changed before the WAV import completed. Reopen Import WAV on the intended track."] = "O projeto mudou antes da conclusão da importação do WAV. Abra Importar WAV novamente na faixa desejada.",
	};

	/// <summary>Registers the audio UI catalog. Call after the base application catalog.</summary>
	public static void Register()
	{
		var english = new Translation { Locale = "en" };
		var portuguese = new Translation { Locale = "pt_BR" };
		foreach (var (source, translated) in Portuguese)
		{
			english.AddMessage(source, source);
			portuguese.AddMessage(source, translated);
		}
		TranslationServer.AddTranslation(english);
		TranslationServer.AddTranslation(portuguese);
	}

	public static string Translate(string source)
	{
		if (TranslationServer.GetLocale() != "pt_BR") return source;
		if (Portuguese.TryGetValue(source, out var translated)) return translated;
		foreach (var (pattern, localizedPattern) in Portuguese)
		{
			if (!pattern.Contains('{')) continue;
			var captures = new List<int>();
			var regex = Regex.Escape(pattern).Replace("\\{", "{").Replace("\\}", "}");
			regex = Regex.Replace(regex, @"\{(\d+)\}", match => { captures.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)); return "(.+?)"; });
			var match = Regex.Match(source, "^" + regex + "$", RegexOptions.CultureInvariant);
			if (!match.Success) continue;
			var args = new object?[captures.Count];
			for (var i = 0; i < captures.Count; i++) args[i] = match.Groups[i + 1].Value;
			try { return string.Format(CultureInfo.CurrentCulture, localizedPattern, args); }
			catch (FormatException) { return source; }
		}
		return source;
	}

	public static string FormatSource(string source, params object?[] args)
	{
		try { return string.Format(CultureInfo.InvariantCulture, source, args); }
		catch (FormatException) { return source; }
	}
}
