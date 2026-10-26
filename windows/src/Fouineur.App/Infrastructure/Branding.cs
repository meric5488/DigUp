using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fouineur.App.Infrastructure;

/// <summary>
/// Identité visuelle de l'entreprise (Ingecap). Le logo est facultatif :
/// déposez Assets\logo-ingecap.png avant de compiler ; sans lui, l'application s'affiche sans logo.
/// </summary>
public static class Branding
{
    public const string CompanyName = "Ingecap";
    public static readonly Color Primary = Color.FromRgb(0x92, 0x4E, 0xEB);   // violet Ingecap
    public static readonly Color Secondary = Color.FromRgb(0x48, 0x27, 0xEC); // bleu-violet Ingecap

    private static ImageSource? _logo;
    private static bool _tried;

    public static ImageSource? Logo
    {
        get
        {
            if (_tried) return _logo;
            _tried = true;
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri("pack://application:,,,/Assets/logo-ingecap.png", UriKind.Absolute);
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                img.Freeze();
                _logo = img;
            }
            catch (Exception) { _logo = null; } // logo absent de la compilation
            return _logo;
        }
    }
}
