<#
    AnyFS test lab: the SharePoint farm as SharePointDsc describes it, and the Local Configuration Manager
    settings it runs under. Dot-sourced by sp-provision.ps1 once the SharePointDsc module is installed
    (PowerShell resolves Import-DscResource when it parses a file, so this cannot sit in the provisioner
    itself). Nothing here runs on its own.
#>

[DscLocalConfigurationManager()]
Configuration AnyFsLcm {
    Node localhost {
        Settings {
            RefreshMode = 'Push'
            ConfigurationMode = 'ApplyOnly'          # the farm is built once; nothing re-checks it every 15 minutes
            RebootNodeIfNeeded = $false              # a restart ends the round; the Vagrantfile does the restarting
            # Not ContinueConfiguration: the LCM would resume the pending configuration at boot on its own, and the
            # provisioner's next round (which applies it again anyway) then collides with it. On the 2019 farm,
            # 18-Oct-2026, the LCM's resumed SPInstall was running SharePoint's setup when the round's own
            # Start-DscConfiguration -Force aborted it (setup kept going, orphaned) and started a second setup,
            # which failed with 'Only one instance of setup may be run at a time' (30066, PreReqCheckFailure).
            ActionAfterReboot = 'StopConfiguration'
        }
    }
}

Configuration AnyFsSharePoint {
    param(
        [Parameter(Mandatory)] [PSCredential]$SetupAccount,
        [Parameter(Mandatory)] [PSCredential]$FarmAccount,
        [Parameter(Mandatory)] [PSCredential]$Passphrase,
        [Parameter(Mandatory)] [string]$MediaDir,
        [Parameter(Mandatory)] [string]$ProductKey,
        [Parameter(Mandatory)] [string]$WebUrl,
        [Parameter(Mandatory)] [string]$AlternateUrl,
        [Parameter(Mandatory)] [string]$Site,
        [Parameter(Mandatory)] [string]$SqlServer,        # this machine, or another farm's machine by address
        [Parameter(Mandatory)] [string]$DatabasePrefix    # SP, SP2019: two farms on one SQL Server keep apart by name
    )
    Import-DscResource -ModuleName PSDesiredStateConfiguration
    Import-DscResource -ModuleName SharePointDsc

    Node localhost {
        SPInstallPrereqs Prerequisites {
            IsSingleInstance = 'Yes'
            InstallerPath    = (Join-Path $MediaDir 'prerequisiteinstaller.exe')
            OnlineMode       = $true
            Ensure           = 'Present'
        }

        SPInstall Binaries {
            IsSingleInstance = 'Yes'
            BinaryDir        = $MediaDir
            ProductKey       = $ProductKey
            Ensure           = 'Present'
            DependsOn        = '[SPInstallPrereqs]Prerequisites'
        }

        SPFarm Farm {
            IsSingleInstance          = 'Yes'
            Ensure                    = 'Present'
            DatabaseServer            = $SqlServer
            FarmConfigDatabaseName    = "${DatabasePrefix}_Config"
            AdminContentDatabaseName  = "${DatabasePrefix}_AdminContent"
            Passphrase                = $Passphrase
            FarmAccount               = $FarmAccount
            RunCentralAdmin           = $true
            CentralAdministrationPort = 9999
            CentralAdministrationAuth = 'NTLM'
            ServerRole                = 'SingleServerFarm'
            PsDscRunAsCredential      = $SetupAccount
            DependsOn                 = '[SPInstall]Binaries'
        }

        SPWebApplication WebApp {
            Name                   = 'AnyFS Lab'
            ApplicationPool        = 'AnyFS Lab'
            ApplicationPoolAccount = $FarmAccount.UserName
            WebAppUrl              = $WebUrl
            Port                   = 80
            DatabaseName           = "${DatabasePrefix}_Content_AnyFS"
            DatabaseServer         = $SqlServer
            AllowAnonymous         = $false
            Ensure                 = 'Present'
            PsDscRunAsCredential   = $SetupAccount
            DependsOn              = '[SPFarm]Farm'
        }

        # The other machines reach the web application by address, not by the name only the domain knows.
        SPAlternateUrl ByAddress {
            WebAppName           = 'AnyFS Lab'
            Zone                 = 'Internet'
            Url                  = $AlternateUrl
            Internal             = $false
            Ensure               = 'Present'
            PsDscRunAsCredential = $SetupAccount
            DependsOn            = '[SPWebApplication]WebApp'
        }

        SPSite Root {
            Url                  = $WebUrl
            OwnerAlias           = $SetupAccount.UserName
            Name                 = 'AnyFS Lab'
            Template             = 'STS#3'
            PsDscRunAsCredential = $SetupAccount
            DependsOn            = '[SPWebApplication]WebApp'
        }

        SPSite Test {
            Url                  = "$WebUrl$Site"
            OwnerAlias           = $SetupAccount.UserName
            Name                 = 'AnyFS'
            Template             = 'STS#3'
            PsDscRunAsCredential = $SetupAccount
            DependsOn            = '[SPSite]Root'
        }

        # The test account's membership of the test site is done by sp-provision.ps1 afterwards (sp-test-user.ps1):
        # a Script resource run as a credential fails with "An item with the same key has already been added".
    }
}
