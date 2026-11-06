using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using WinLayerBoot.Core;

namespace WinLayerBoot.Customize;

/// <summary>Remote Desktop: unattend file specialize pass.</summary>
public sealed class Rdp : Customization
{
    public override string Key => "Rdp";
    public override string Label => "Remote Desktop";
    public override string Help => "Unattend file specialize: fDenyTSConnections=false";
    public override bool HasStep => false;
    public override string UnattendSpecialize(LayerPlan p, Values v) =>
        """
                <component name="Microsoft-Windows-TerminalServices-LocalSessionManager" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
                  <fDenyTSConnections>false</fDenyTSConnections>
                </component>

            """;
}

/// <summary>Turns off the firewall for the three network profiles: unattend file specialize pass.</summary>
public sealed class NoFirewall : Customization
{
    public override string Key => "NoFirewall";
    public override string Label => "Turn off firewall";
    public override string Help => "Unattend file specialize: domain, private and public profiles all turned off";
    public override bool HasStep => false;
    public override string UnattendSpecialize(LayerPlan p, Values v) =>
        """
                <component name="Networking-MPSSVC-Svc" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
                  <DomainProfile_EnableFirewall>false</DomainProfile_EnableFirewall>
                  <PrivateProfile_EnableFirewall>false</PrivateProfile_EnableFirewall>
                  <PublicProfile_EnableFirewall>false</PublicProfile_EnableFirewall>
                </component>

            """;
}

/// <summary>Adds an English input method (US keyboard): only when the base disk is not an English system. Written into the unattend file's InputLocale.</summary>
public sealed class EnglishInput : Customization
{
    public override string Key => "EnInput";
    public override string Label => "Also add English input method (US keyboard)";
    public override bool HasStep => false;
    public override bool Applies(LayerPlan p) => p.Intl is null || !p.Intl.UI.StartsWith("en-", StringComparison.OrdinalIgnoreCase);
    public override string NotApplicable(LayerPlan p) => "The base disk is an English system, no extra English input method needed";
}

/// <summary>Unattend file: account, base disk language + optional English input method, skip OOBE; adds specialize passes per the selected customizations.</summary>
public static class Unattend
{
    /// <param name="bootLocale">Boot menu language. On first boot Setup changes the locale of {bootmgr} and the current boot entry to the system language (it changes the BCD used for booting); changed back at first logon.</param>
    public static string Build(LayerPlan p, Values v, string user, string pass, string bootLocale)
    {
        var intl = p.Intl;
        var enInput = Customization.All.OfType<EnglishInput>().First() is var ei && ei.Applies(p) && ei.On(v);
        var inputLocale = enInput ? $"{intl?.Keyboard};0409:00000409" : intl?.Keyboard ?? "";
        var timeZone = intl is not null && intl.TimeZone != "" ? intl.TimeZone : "UTC";
        var spec = string.Concat(Customization.All.Where(c => c.Applies(p) && c.On(v)).Select(c => c.UnattendSpecialize(p, v)));
        var accounts = p.IsServer
            ? $"""
                        <AdministratorPassword>
                          <Value>{pass}</Value>
                          <PlainText>true</PlainText>
                        </AdministratorPassword>

                """
            : $"""
                        <LocalAccounts>
                          <LocalAccount wcm:action="add">
                            <Name>{user}</Name>
                            <DisplayName>{user}</DisplayName>
                            <Group>Administrators</Group>
                            <Password>
                              <Value>{pass}</Value>
                              <PlainText>true</PlainText>
                            </Password>
                          </LocalAccount>
                        </LocalAccounts>

                """;
        var specXml = spec.Length > 0 ? $"  <settings pass=\"specialize\">\r\n{spec}  </settings>\r\n" : "";
        var localeCommands = string.Concat(new[] { "{bootmgr}", "{current}" }.Select((id, i) =>
            $"""
                    <SynchronousCommand wcm:action="add">
                      <Order>{i + 1}</Order>
                      <CommandLine>bcdedit /set {id} locale {bootLocale}</CommandLine>
                    </SynchronousCommand>

            """));
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
            {specXml}  <settings pass="oobeSystem">
                <component name="Microsoft-Windows-International-Core" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
                  <InputLocale>{inputLocale}</InputLocale>
                  <SystemLocale>{intl?.SysLocale}</SystemLocale>
                  <UILanguage>{intl?.UI}</UILanguage>
                  <UserLocale>{intl?.UserLocale}</UserLocale>
                </component>
                <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
                  <TimeZone>{timeZone}</TimeZone>
                  <OOBE>
                    <HideEULAPage>true</HideEULAPage>
                    <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>
                    <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
                    <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
                    <ProtectYourPC>3</ProtectYourPC>
                  </OOBE>
                  <UserAccounts>
            {accounts}      </UserAccounts>
                  <AutoLogon>
                    <Enabled>true</Enabled>
                    <LogonCount>1</LogonCount>
                    <Username>{user}</Username>
                    <Password>
                      <Value>{pass}</Value>
                      <PlainText>true</PlainText>
                    </Password>
                  </AutoLogon>
                  <FirstLogonCommands>
            {localeCommands}      </FirstLogonCommands>
                </component>
              </settings>
            </unattend>
            """;
        return Regex.Replace(xml, "\r?\n", "\r\n");
    }

    public static string InputLocale(LayerPlan p, Values v)
    {
        var ei = Customization.All.OfType<EnglishInput>().First();
        return ei.Applies(p) && ei.On(v) ? $"{p.Intl?.Keyboard};0409:00000409" : p.Intl?.Keyboard ?? "";
    }
}
