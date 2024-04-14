using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Win32;
using Windows.Win32.Foundation;

using WinRT.Interop;

namespace AzureReswTranslator;

public sealed partial class MainWindow : Window
{
    private const string cAzureEndPoint = "https://api.cognitive.microsofttranslator.com";

    private readonly IntPtr windowPtr;
    private StorageFile? sourceFile;

    private static readonly HttpClient httpClient = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) });

    public MainWindow()
    {
        this.InitializeComponent();
        windowPtr = WindowNative.GetWindowHandle(this);

        AppWindow.Title = "Azure Resw Translator";

        CenterInPrimaryDisplay(clientWidth: 1060, clientHeight: 255);

        LayoutRoot.Loaded += LayoutRoot_Loaded;
    }

    private void CenterInPrimaryDisplay(int clientWidth, int clientHeight)
    {
        double scaleFactor = PInvoke.GetDpiForWindow((HWND)windowPtr) / 96.0;

        int deviceWidth = (int)(clientWidth * scaleFactor);
        int deviceHeight = (int)(clientHeight * scaleFactor);

        RectInt32 windowArea;
        RectInt32 workArea = DisplayArea.Primary.WorkArea;

        windowArea.X = Math.Max((workArea.Width - deviceWidth) / 2, workArea.X);
        windowArea.Y = Math.Max((workArea.Height - deviceHeight) / 2, workArea.Y);
        windowArea.Width = deviceWidth;
        windowArea.Height = deviceHeight;

        AppWindow.MoveAndResize(windowArea);
    }


    private async void LayoutRoot_Loaded(object sender, RoutedEventArgs e)
    {
        Dictionary<string, LanguageData> languages = await Task.Run(LoadLanguages);

        if (languages.Count == 0)
        {
            await ErrorDialog("Failed to read available languages.");
        }
        else
        {
            FromLanguage.ItemsSource = languages.Values;
            ToLanguage.ItemsSource = languages.Values;

            if (languages.TryGetValue("en", out LanguageData? fromValue))
            {
                FromLanguage.SelectedItem = fromValue;
            }

            if (languages.TryGetValue("fr", out LanguageData? toValue))
            {
                ToLanguage.SelectedItem = toValue;
            }
        }
    }

    private async void SelectFile_Click(object sender, RoutedEventArgs e)
    {
        FileOpenPicker openPicker = new FileOpenPicker();
        InitializeWithWindow.Initialize(openPicker, windowPtr);
        openPicker.FileTypeFilter.Add(".resw");

        StorageFile newSource = await openPicker.PickSingleFileAsync();

        if (newSource is not null)
        {
            sourceFile = newSource;
            SourceReswPath.Text = sourceFile.Path;
        }
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await IsDataValid())
            {
                await using (Stream stream = await sourceFile.OpenStreamForReadAsync())
                {
                    XDocument? document;

                    try
                    {
                        document = await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        await ErrorDialog($"Loading source resw failed, exception:{Environment.NewLine}{ex}");
                        return;
                    }

                    if (await ValidateReswVersion(document))
                    {
                        List<SourceEntry> source = ParseSource(document);
                        List<string> translated = await TranslateSource(source);

                        if (translated.Count == source.Count)  // translation succeeded
                        {
                            FileSavePicker savePicker = new FileSavePicker();
                            InitializeWithWindow.Initialize(savePicker, windowPtr);
                            savePicker.FileTypeChoices.Add("resw file", [".resw"]);

                            savePicker.SuggestedFileName = sourceFile?.Name;

                            StorageFile outputFile = await savePicker.PickSaveFileAsync();

                            if (outputFile is not null)
                            {
                                await WriteToOutputResw(document, translated, outputFile);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            await ErrorDialog($"General failure:{Environment.NewLine}{ex}");
        }
    }

    private sealed class SourceEntry
    {
        public SourceEntry(string text)
        {
            Text = text;
        }

        [JsonInclude]
        public string Text { get; set; }
    }

    private static List<SourceEntry> ParseSource(XDocument document)
    {
        List<SourceEntry> source = [];

        foreach (XElement data in document.Descendants("data"))
        {
            IEnumerable<XElement> values = data.Descendants("value");
            Debug.Assert(values.Count() == 1);

            foreach (XElement value in values)
            {
                source.Add(new SourceEntry(value.Value));
            }
        }

        return source;
    }

    private async Task<bool> ValidateReswVersion(XDocument document)
    {
        if (document.Root is not null)
        {
            foreach (XElement resHeader in document.Descendants("resheader"))
            {
                XAttribute? nameAttrib = resHeader.Attribute("name");

                if ((nameAttrib != null) && (nameAttrib.Value == "version") && (resHeader.Value == "2.0"))
                {
                    return true;
                }
            }
        }

        await ErrorDialog("Invalid resw xml");
        return false;
    }

    private async Task<Dictionary<string, LanguageData>> LoadLanguages()
    {
        using (HttpRequestMessage request = new())
        {
            request.Method = HttpMethod.Get;
            request.RequestUri = new Uri(cAzureEndPoint + "/languages?api-version=3.0&scope=translation");

            try
            {
                HttpResponseMessage response = await httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    string result = await response.Content.ReadAsStringAsync();

                    LanguageRoot? root = JsonSerializer.Deserialize<LanguageRoot>(result);

                    if (root is not null)
                    {
                        foreach (KeyValuePair<string, LanguageData> pair in root.Translation)
                        {
                            pair.Value.Code = pair.Key;
                        }

                        return root.Translation;
                    }
                }
            }
            catch (Exception ex) // likely net's down
            {
                Debug.WriteLine(ex.ToString());
            }
        }

        return new();
    }

    private sealed class LanguageRoot
    {
        [JsonPropertyName("translation")]
        public Dictionary<string, LanguageData> Translation { get; set; } = new();
    }

    private sealed class LanguageData
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("nativeName")]
        public string NativeName { get; set; } = string.Empty;

        [JsonPropertyName("dir")]
        public string Direction { get; set; } = string.Empty;

        [JsonIgnore]
        public string Code { get; set; } = string.Empty;

        public override string ToString()
        {
            return $"{Code} - {Name}";
        }
    }

    private async Task<List<string>> TranslateSource(List<SourceEntry> source)
    {
        // code based on:
        // https://learn.microsoft.com/en-us/azure/ai-services/Translator/translator-text-apis?tabs=csharp#translate-text

        List<string> results = new List<string>(source.Count);

        if (source.Count > 0)
        {
            string requestBody = JsonSerializer.Serialize(source);
            string route = $"/translate?api-version=3.0&from={((LanguageData)FromLanguage.SelectedItem).Code}&to={((LanguageData)ToLanguage.SelectedItem).Code}";

            using (HttpRequestMessage request = new())
            {
                // Build the request.
                request.Method = HttpMethod.Post;
                request.RequestUri = new Uri(cAzureEndPoint + route);
                request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                request.Headers.Add("Ocp-Apim-Subscription-Key", AzureKey.Text);
                request.Headers.Add("Ocp-Apim-Subscription-Region", AzureRegion.Text);

                // Send the request and get response.
                HttpResponseMessage response = await httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    // Read response as a string.
                    string result = await response.Content.ReadAsStringAsync();

                    List<TranslationRoot>? roList = JsonSerializer.Deserialize<List<TranslationRoot>>(result);
                    Debug.Assert(roList is not null);

                    if (roList is not null)
                    {
                        Debug.Assert(roList.Count == source.Count);

                        foreach (TranslationRoot root in roList)
                        {
                            foreach (Translation translation in root.Translations)
                            {
                                results.Add(translation.Text);
                            }
                        }
                    }
                }
                else
                {
                    await ErrorDialog($"Translation failed, Http error: {response.StatusCode}");
                }
            }
        }

        return results;
    }


    private sealed class TranslationRoot
    {
        [JsonPropertyName("translations")]
        public List<Translation> Translations { get; set; } = [];
    }

    private sealed class Translation
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("to")]
        public string To { get; set; } = string.Empty;
    }

    private static async Task WriteToOutputResw(XDocument document, List<string> text, StorageFile outputFile)
    {
        int index = 0;

        foreach (XElement data in document.Descendants("data"))
        {
            IEnumerable<XElement> values = data.Descendants("value");
            Debug.Assert(values.Count() == 1);

            foreach (XElement value in values)
            {
                value.Value = text[index++];
            }
        }

        using (Stream stream = await outputFile.OpenStreamForWriteAsync())
        {
            await document.SaveAsync(stream, SaveOptions.None, CancellationToken.None);
            stream.SetLength(stream.Position);
        }
    }

    private async Task<bool> IsDataValid()
    {
        if (string.IsNullOrWhiteSpace(SourceReswPath.Text))
        {
            await ErrorDialog("Source file is empty");
            return false;
        }

        if ((sourceFile == null) || !string.Equals(sourceFile.Path, SourceReswPath.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                sourceFile = await StorageFile.GetFileFromPathAsync(SourceReswPath.Text.Trim());
            }
            catch
            {
                await ErrorDialog("Source file path is invalid");
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(AzureRegion.Text))
        {
            await ErrorDialog("Azure region is invalid");
            return false;
        }

        if (string.IsNullOrWhiteSpace(AzureKey.Text))
        {
            await ErrorDialog("Azure key is invalid");
            return false;
        }

        if ((FromLanguage.SelectedIndex < 0) || (ToLanguage.SelectedIndex < 0))
        {
            await ErrorDialog("languages are invalid");
            return false;
        }

        if (FromLanguage.SelectedIndex == ToLanguage.SelectedIndex)
        {
            await ErrorDialog("languages are equal");
            return false;
        }

        return true;
    }

    private async Task ErrorDialog(string message)
    {
        ContentDialog cd = new ContentDialog()
        {
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            XamlRoot = Content.XamlRoot,
            Title = AppWindow.Title,
            PrimaryButtonText = "OK",
            DefaultButton = ContentDialogButton.Primary,
            Content = message,
        };

        await cd.ShowAsync();
    }
}
