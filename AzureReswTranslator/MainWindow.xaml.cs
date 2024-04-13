using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
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
    private readonly IntPtr windowPtr;
    private StorageFile? sourceFile;

    public MainWindow()
    {
        this.InitializeComponent();
        windowPtr = WindowNative.GetWindowHandle(this);

        AppWindow.Title = "Azure Resw Translator";

        CenterInPrimaryDisplay(clientWidth: 1060, clientHieght: 300);

        LayoutRoot.Loaded += LayoutRoot_Loaded;
    }

    private void CenterInPrimaryDisplay(int clientWidth, int clientHieght)
    {
        double scaleFactor = PInvoke.GetDpiForWindow((HWND)windowPtr) / 96.0 ;

        int deviceWidth = (int)(clientWidth * scaleFactor);
        int deviceHeight = (int)(clientHieght * scaleFactor);

        RectInt32 windowArea;
        RectInt32 workArea = DisplayArea.Primary.WorkArea;

        windowArea.X = Math.Max((workArea.Width - deviceWidth) / 2, workArea.X);
        windowArea.Y = Math.Max((workArea.Height - deviceHeight) / 2, workArea.Y);
        windowArea.Width = deviceWidth;
        windowArea.Height = deviceHeight;

        AppWindow.MoveAndResize(windowArea);
    }


    private void LayoutRoot_Loaded(object sender, RoutedEventArgs e)
    {
        FromLanguage.SelectedIndex = LanguageCodes.FindIndex(x => x.Code == "en");
        ToLanguage.SelectedIndex = LanguageCodes.FindIndex(x => x.Code == "fr");
    }

    private async void SelectFile_Click(object sender, RoutedEventArgs e)
    {
        FileOpenPicker openPicker = new FileOpenPicker();
        InitializeWithWindow.Initialize(openPicker, windowPtr);
        openPicker.FileTypeFilter.Add(".resw");

        sourceFile = await openPicker.PickSingleFileAsync();

        SourceReswPath.Text = (sourceFile is not null) ? sourceFile.Path : string.Empty;
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
                                WriteToOutputResw(document, translated, outputFile);
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
            XAttribute? nameAttrib = data.Attribute("name");

            if (nameAttrib is not null)
            {
                source.Add(new SourceEntry(data.Value.Trim()));
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

    private async Task<List<string>> TranslateSource(List<SourceEntry> source)
    {
        // code based on:
        // https://learn.microsoft.com/en-us/azure/ai-services/Translator/translator-text-apis?tabs=csharp#translate-text

        List<string> results = new List<string>(source.Count);

        if (source.Count > 0)
        {
            string requestBody = JsonSerializer.Serialize(source);
            string route = $"/translate?api-version=3.0&from={LanguageCodes[FromLanguage.SelectedIndex].Code}&to={LanguageCodes[ToLanguage.SelectedIndex].Code}";

            using (HttpClient client = new())
            {
                using (HttpRequestMessage request = new())
                {
                    // Build the request.
                    request.Method = HttpMethod.Post;
                    request.RequestUri = new Uri(AzureEndPoint.Text + route);
                    request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                    request.Headers.Add("Ocp-Apim-Subscription-Key", AzureKey.Text);
                    request.Headers.Add("Ocp-Apim-Subscription-Region", AzureRegion.Text);

                    // Send the request and get response.
                    HttpResponseMessage response = await client.SendAsync(request);

                    if (response.IsSuccessStatusCode)
                    {
                        // Read response as a string.
                        string result = await response.Content.ReadAsStringAsync();

                        List<RootObject>? roList = JsonSerializer.Deserialize<List<RootObject>>(result);
                        Debug.Assert(roList is not null);

                        if (roList is not null)
                        {
                            Debug.Assert(roList.Count == source.Count);

                            foreach (RootObject root in roList)
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
        }

        return results;
    }


    public sealed class RootObject
    {
        [JsonPropertyName("translations")]
        public List<Translation> Translations { get; set; } = [];
    }

    public sealed class Translation
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("to")]
        public string To { get; set; } = string.Empty;
    }

    private static void WriteToOutputResw(XDocument document, List<string> text, StorageFile outputFile)
    {
        // I'd like to just parse the xml document, replacing values with the translated text. However
        // when saving the xml document, the <value> element is removed and the content inlined. It's 
        // valid xml, but the pri compiler obviously isn't loading the file as xml because wthout the <value> 
        // element it doesn't include any of the translations. Need to rebuild the resw file as text...
        const string preamble = """
            <?xml version="1.0" encoding="utf-8"?>
            <root>
              <!-- 
                Microsoft ResX Schema 

                Version 2.0

                The primary goals of this format is to allow a simple XML format 
                that is mostly human readable. The generation and parsing of the 
                various data types are done through the TypeConverter classes 
                associated with the data types.

                Example:

                ... ado.net/XML headers & schema ...
                <resheader name="resmimetype">text/microsoft-resx</resheader>
                <resheader name="version">2.0</resheader>
                <resheader name="reader">System.Resources.ResXResourceReader, System.Windows.Forms, ...</resheader>
                <resheader name="writer">System.Resources.ResXResourceWriter, System.Windows.Forms, ...</resheader>
                <data name="Name1"><value>this is my long string</value><comment>this is a comment</comment></data>
                <data name="Color1" type="System.Drawing.Color, System.Drawing">Blue</data>
                <data name="Bitmap1" mimetype="application/x-microsoft.net.object.binary.base64">
                    <value>[base64 mime encoded serialized .NET Framework object]</value>
                </data>
                <data name="Icon1" type="System.Drawing.Icon, System.Drawing" mimetype="application/x-microsoft.net.object.bytearray.base64">
                    <value>[base64 mime encoded string representing a byte array form of the .NET Framework object]</value>
                    <comment>This is a comment</comment>
                </data>

                There are any number of "resheader" rows that contain simple 
                name/value pairs.

                Each data row contains a name, and value. The row also contains a 
                type or mimetype. Type corresponds to a .NET class that support 
                text/value conversion through the TypeConverter architecture. 
                Classes that don't support this are serialized and stored with the 
                mimetype set.

                The mimetype is used for serialized objects, and tells the 
                ResXResourceReader how to depersist the object. This is currently not 
                extensible. For a given mimetype the value must be set accordingly:

                Note - application/x-microsoft.net.object.binary.base64 is the format 
                that the ResXResourceWriter will generate, however the reader can 
                read any of the formats listed below.

                mimetype: application/x-microsoft.net.object.binary.base64
                value   : The object must be serialized with 
                        : System.Runtime.Serialization.Formatters.Binary.BinaryFormatter
                        : and then encoded with base64 encoding.

                mimetype: application/x-microsoft.net.object.soap.base64
                value   : The object must be serialized with 
                        : System.Runtime.Serialization.Formatters.Soap.SoapFormatter
                        : and then encoded with base64 encoding.

                mimetype: application/x-microsoft.net.object.bytearray.base64
                value   : The object must be serialized into a byte array 
                        : using a System.ComponentModel.TypeConverter
                        : and then encoded with base64 encoding.
                -->
              <xsd:schema id="root" xmlns="" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:msdata="urn:schemas-microsoft-com:xml-msdata">
                <xsd:import namespace="http://www.w3.org/XML/1998/namespace" />
                <xsd:element name="root" msdata:IsDataSet="true">
                  <xsd:complexType>
                    <xsd:choice maxOccurs="unbounded">
                      <xsd:element name="metadata">
                        <xsd:complexType>
                          <xsd:sequence>
                            <xsd:element name="value" type="xsd:string" minOccurs="0" />
                          </xsd:sequence>
                          <xsd:attribute name="name" use="required" type="xsd:string" />
                          <xsd:attribute name="type" type="xsd:string" />
                          <xsd:attribute name="mimetype" type="xsd:string" />
                          <xsd:attribute ref="xml:space" />
                        </xsd:complexType>
                      </xsd:element>
                      <xsd:element name="assembly">
                        <xsd:complexType>
                          <xsd:attribute name="alias" type="xsd:string" />
                          <xsd:attribute name="name" type="xsd:string" />
                        </xsd:complexType>
                      </xsd:element>
                      <xsd:element name="data">
                        <xsd:complexType>
                          <xsd:sequence>
                            <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
                            <xsd:element name="comment" type="xsd:string" minOccurs="0" msdata:Ordinal="2" />
                          </xsd:sequence>
                          <xsd:attribute name="name" type="xsd:string" use="required" msdata:Ordinal="1" />
                          <xsd:attribute name="type" type="xsd:string" msdata:Ordinal="3" />
                          <xsd:attribute name="mimetype" type="xsd:string" msdata:Ordinal="4" />
                          <xsd:attribute ref="xml:space" />
                        </xsd:complexType>
                      </xsd:element>
                      <xsd:element name="resheader">
                        <xsd:complexType>
                          <xsd:sequence>
                            <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
                          </xsd:sequence>
                          <xsd:attribute name="name" type="xsd:string" use="required" />
                        </xsd:complexType>
                      </xsd:element>
                    </xsd:choice>
                  </xsd:complexType>
                </xsd:element>
              </xsd:schema>
              <resheader name="resmimetype">
                <value>text/microsoft-resx</value>
              </resheader>
              <resheader name="version">
                <value>2.0</value>
              </resheader>
              <resheader name="reader">
                <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
              </resheader>
              <resheader name="writer">
                <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
              </resheader>
            """;

        const string postamble = "</root>";

        const string template = """
              <data name="{0}" xml:space="preserve">
                <value>{1}</value>
              </data>
            """;

        StringBuilder sb = new StringBuilder(1024 * 15);

        sb.AppendLine(preamble);

        int index = 0;

        foreach (XElement data in document.Descendants("data"))
        {
            XAttribute? name = data.Attribute("name");
            Debug.Assert(name is not null);

            if ((index < text.Count) && (name is not null))
            {
                sb.AppendLine(string.Format(template, name.Value, text[index++]));
            }
        }

        sb.AppendLine(postamble);

        File.WriteAllText(outputFile.Path, sb.ToString(), Encoding.UTF8);
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

        if (string.IsNullOrWhiteSpace(AzureEndPoint.Text) ||
            !Uri.TryCreate(AzureEndPoint.Text, UriKind.Absolute, out Uri? uri) ||
            !((uri is not null) && (uri.Scheme == Uri.UriSchemeHttps)))
        {
            await ErrorDialog("Azure end point is invalid");
            return false;
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

    private record LanguageCode(string Code, string Description)
    {
        public override string ToString()
        {
            return $"{Description} - {Code}";
        }
    }

    // common laguage codes
    // a full list can be found at:
    // https://www.iana.org/assignments/language-subtag-registry/language-subtag-registry

    private readonly List<LanguageCode> LanguageCodes = new List<LanguageCode>()
    {
        new LanguageCode("af", "Afrikaans"),
        new LanguageCode("ar", "Arabic"),
        new LanguageCode("az", "Azeri"),
        new LanguageCode("be", "Belarusian"),
        new LanguageCode("bg", "Bulgarian"),
        new LanguageCode("ca", "Catalan"),
        new LanguageCode("cs", "Czech"),
        new LanguageCode("cy", "Welsh"),
        new LanguageCode("da", "Danish"),
        new LanguageCode("de", "German"),
        new LanguageCode("dv", "Divehi"),
        new LanguageCode("el", "Greek"),
        new LanguageCode("en", "English"),
        new LanguageCode("eo", "Esperanto"),
        new LanguageCode("es", "Spanish"),
        new LanguageCode("et", "Estonian"),
        new LanguageCode("eu", "Basque"),
        new LanguageCode("fa", "Farsi"),
        new LanguageCode("fi", "Finnish"),
        new LanguageCode("fo", "Faroese"),
        new LanguageCode("fr", "French"),
        new LanguageCode("gl", "Galician"),
        new LanguageCode("gu", "Gujarati"),
        new LanguageCode("he", "Hebrew"),
        new LanguageCode("hi", "Hindi"),
        new LanguageCode("hr", "Croatian"),
        new LanguageCode("hu", "Hungarian"),
        new LanguageCode("hy", "Armenian"),
        new LanguageCode("id", "Indonesian"),
        new LanguageCode("is", "Icelandic"),
        new LanguageCode("it", "Italian"),
        new LanguageCode("ja", "Japanese"),
        new LanguageCode("ka", "Georgian"),
        new LanguageCode("kk", "Kazakh"),
        new LanguageCode("kn", "Kannada"),
        new LanguageCode("ko", "Korean"),
        new LanguageCode("kok", "Konkani"),
        new LanguageCode("ky", "Kyrgyz"),
        new LanguageCode("lt", "Lithuanian"),
        new LanguageCode("lv", "Latvian"),
        new LanguageCode("mi", "Maori"),
        new LanguageCode("mk", "FYRO Macedonian"),
        new LanguageCode("mn", "Mongolian"),
        new LanguageCode("mr", "Marathi"),
        new LanguageCode("ms", "Malay"),
        new LanguageCode("mt", "Maltese"),
        new LanguageCode("nb", "Norwegian"),
        new LanguageCode("nl", "Dutch"),
        new LanguageCode("ns", "Northern Sotho"),
        new LanguageCode("pa", "Punjabi"),
        new LanguageCode("pl", "Polish"),
        new LanguageCode("ps", "Pashto"),
        new LanguageCode("pt", "Portuguese"),
        new LanguageCode("qu", "Quechua"),
        new LanguageCode("ro", "Romanian"),
        new LanguageCode("ru", "Russian"),
        new LanguageCode("sa", "Sanskrit"),
        new LanguageCode("se", "Sami"),
        new LanguageCode("sk", "Slovak"),
        new LanguageCode("sl", "Slovenian"),
        new LanguageCode("sq", "Albanian"),
        new LanguageCode("sv", "Swedish"),
        new LanguageCode("sw", "Swahili"),
        new LanguageCode("syr", "Syriac"),
        new LanguageCode("ta", "Tamil"),
        new LanguageCode("te", "Telugu"),
        new LanguageCode("th", "Thai"),
        new LanguageCode("tl", "Tagalog"),
        new LanguageCode("tn", "Tswana"),
        new LanguageCode("tr", "Turkish"),
        new LanguageCode("tt", "Tatar"),
        new LanguageCode("ts", "Tsonga"),
        new LanguageCode("uk", "Ukrainian"),
        new LanguageCode("ur", "Urdu"),
        new LanguageCode("uz", "Uzbek"),
        new LanguageCode("vi", "Vietnamese"),
        new LanguageCode("xh", "Xhosa"),
        new LanguageCode("zh", "Chinese"),
        new LanguageCode("zh-Hans", "Chinese simplified script"),
        new LanguageCode("zh-Hant", "Chinese traditional script"),
        new LanguageCode("zh-CN", "Chinese (mainland)"),
        new LanguageCode("zh-HK", "Chinese (Hong Kong)"),
        new LanguageCode("zh-MO", "Chinese (Macau)"),
        new LanguageCode("zh-SG", "Chinese (Singapore)"),
        new LanguageCode("zh-TW", "Chinese (Taiwan)"),
        new LanguageCode("zu", "Zulu"),
    };
}