using Godot;

/// <summary>Translations for runtime theme editor feedback.</summary>
public static class ThemeTranslations
{
    public static void Register(Translation english, Translation portuguese)
    {
        (string Source, string Portuguese)[] entries =
        {
            ("Could not create the theme preview.", "Não foi possível criar a prévia do tema."),
            ("Preview only — save to keep these colors.", "Apenas prévia — salve para manter estas cores."),
            ("Project defaults can only be saved from the editable project.", "Os padrões do projeto só podem ser salvos no projeto editável."),
            ("Palette saved for the standalone editor. It is not applied at app startup.", "Paleta salva para o editor independente. Ela não é aplicada ao iniciar o aplicativo."),
            ("Could not save palette (error %d).", "Não foi possível salvar a paleta (erro %d)."),
            ("Restored the last saved palette.", "A última paleta salva foi restaurada.")
        };
        foreach (var (source, translated) in entries)
        {
            english.AddMessage(source, source);
            portuguese.AddMessage(source, translated);
        }
    }
}
