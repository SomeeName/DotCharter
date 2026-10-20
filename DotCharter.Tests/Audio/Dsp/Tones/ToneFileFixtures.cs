namespace DotCharter.Tests.Audio.Dsp.Tones;

/// <summary>
///     The three tone-file shapes DLC Builder (Rocksmith2014.NET) writes, copied from its own
///     tests: <c>test.tone2014.xml</c> (the Toolkit-compatible DataContract export), a tone JSON
///     body (<c>Tone.exportJson</c>: string Volume, <c>Key</c> on a pedal, skins) and the
///     <c>Tones</c> array of a <c>.rs2dlc</c> project.
/// </summary>
internal static class ToneFileFixtures
{
    public const string ToolkitXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Tone2014 xmlns:i="http://www.w3.org/2001/XMLSchema-instance" xmlns="http://schemas.datacontract.org/2004/07/RocksmithToolkitLib.DLCPackage.Manifest2014.Tone">
          <GearList>
            <Amp>
              <Category>Amp</Category>
              <KnobValues xmlns:d4p1="http://schemas.microsoft.com/2003/10/Serialization/Arrays">
                <d4p1:KeyValueOfstringfloat>
                  <d4p1:Key>Amp_OrangeAD50_Gain</d4p1:Key>
                  <d4p1:Value>88</d4p1:Value>
                </d4p1:KeyValueOfstringfloat>
                <d4p1:KeyValueOfstringfloat>
                  <d4p1:Key>Amp_OrangeAD50_Bass</d4p1:Key>
                  <d4p1:Value>75.5</d4p1:Value>
                </d4p1:KeyValueOfstringfloat>
              </KnobValues>
              <PedalKey>Amp_OrangeAD50</PedalKey>
              <Skin i:nil="true" />
              <SkinIndex i:nil="true" />
              <Type>Amps</Type>
            </Amp>
            <Cabinet>
              <Category i:nil="true" />
              <KnobValues xmlns:d4p1="http://schemas.microsoft.com/2003/10/Serialization/Arrays" />
              <PedalKey>Cab_OrangeJimmyBean_57_Cone</PedalKey>
              <Skin i:nil="true" />
              <SkinIndex i:nil="true" />
              <Type>Cabinets</Type>
            </Cabinet>
            <PostPedal1 i:nil="true" />
            <PostPedal2 i:nil="true" />
            <PostPedal3 i:nil="true" />
            <PostPedal4 i:nil="true" />
            <PrePedal1 i:nil="true" />
            <PrePedal2>
              <Category i:nil="true" />
              <KnobValues xmlns:d4p1="http://schemas.microsoft.com/2003/10/Serialization/Arrays">
                <d4p1:KeyValueOfstringfloat>
                  <d4p1:Key>Pedal_Chorus20_Mix</d4p1:Key>
                  <d4p1:Value>8</d4p1:Value>
                </d4p1:KeyValueOfstringfloat>
              </KnobValues>
              <PedalKey>Pedal_Chorus20</PedalKey>
              <Type>Pedals</Type>
            </PrePedal2>
            <PrePedal3 i:nil="true" />
            <PrePedal4 i:nil="true" />
            <Rack1 i:nil="true" />
            <Rack2 i:nil="true" />
            <Rack3 i:nil="true" />
            <Rack4 i:nil="true" />
          </GearList>
          <IsCustom>true</IsCustom>
          <Key>Test</Key>
          <Name>Test Tone</Name>
          <NameSeparator> - </NameSeparator>
          <SortOrder>0</SortOrder>
          <ToneDescriptors xmlns:d2p1="http://schemas.microsoft.com/2003/10/Serialization/Arrays">
            <d2p1:string>$[35720]CLEAN</d2p1:string>
          </ToneDescriptors>
          <Volume>-12</Volume>
        </Tone2014>
        """;

    /// <summary>One tone body as DLC Builder writes it (and as a project stores it).</summary>
    public static string ToneBody(string key, string volume = "-19.800") => $$"""
        {
          "GearList": {
            "Amp": {
              "Type": "Amps",
              "KnobValues": { "Amp_OrangeAD50_Bass": 40, "Amp_OrangeAD50_Gain": 60 },
              "Key": "Amp_OrangeAD50",
              "Skin": "urn:image:dds:gear_amp_orangead50_2",
              "SkinIndex": 2
            },
            "Cabinet": {
              "Type": "Cabinets",
              "KnobValues": {},
              "Key": "Cab_OrangePPC212OB_Condenser_Cone"
            },
            "PostPedal1": {
              "Type": "Pedals",
              "KnobValues": { "Pedal_SpringReverb_Depth": 50, "Pedal_SpringReverb_Mix": 20 },
              "Key": "Pedal_SpringReverb",
              "Category": "Reverb"
            }
          },
          "ToneDescriptors": [ "$[35751]MULTI-EFFECT", "$[35722]DISTORTION" ],
          "NameSeparator": " - ",
          "IsCustom": true,
          "Volume": "{{volume}}",
          "Key": "{{key}}",
          "Name": "{{key}}"
        }
        """;

    /// <summary>A project file: arrangements and metadata around a <c>Tones</c> array.</summary>
    public static string Project(params string[] bodies) => $$"""
        {
          "Version": "1",
          "DLCKey": "IntegrationTest",
          "ArtistName": "Artist",
          "Title": "Title",
          "Arrangements": [
            { "Case": "Instrumental", "Fields": [ { "XML": "arr_guitar_RS2.xml", "BaseTone": "guitar", "Tones": [] } ] }
          ],
          "Tones": [ {{string.Join(",", bodies)}} ]
        }
        """;
}
