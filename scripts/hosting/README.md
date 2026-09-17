# Team Hub interactive Windows hosting

Run **Host-TeamHub.bat** from the repository root on the Windows server. The
batch file opens the PowerShell installer, requests administrator elevation, and
keeps the final result visible.

To prepare a deployment package on a development machine, run
**Publish-TeamHub.bat** from the repository root. Press Enter to accept its
timestamped output directory. The resulting folder contains the published web
application and default `config` directory, but never includes local `data` or
development databases. Copy the entire folder to the server and select it when
the hosting installer asks for the source repository or published package.

## What the installer handles

- Installs the IIS role when it is missing.
- Verifies ASP.NET Core Module V2 and the .NET 10 ASP.NET Core runtime.
- Prompts for a local .NET 10 Hosting Bundle installer when required.
- Publishes Team Hub from a source checkout, or accepts a pre-published folder.
- Stops only the selected Team Hub IIS site and application pool during updates.
- Creates a timestamped backup of the existing data and config directories.
- Deploys application files without overwriting data or config.
- Optionally migrates an existing Team Hub data directory.
- Creates one IIS application pool with a single worker process for SQLite.
- Configures a DNS host name, an installed HTTPS certificate, and a Domain/Private
  Windows Firewall rule.
- Grants the application pool read access to the app and modify access to data.
- Creates the first administrator without saving its password in the deployment.
- Optionally stores Microsoft Graph settings in the selected IIS application
  pool's environment-variable collection.
- Writes a detailed transcript to the current operator's temporary directory.

## Information to prepare

1. Team Hub source-repository path or a folder produced by dotnet publish.
2. Desired IIS site name, application-pool name, and installation directory.
3. Internal DNS host name, such as teamhub.company.com.
4. A company certificate installed under Local Computer > Personal, including
   its private key and thumbprint.
5. The local .NET 10 Hosting Bundle installer if the server does not have it.
6. The initial administrator email/user ID, display name, and password.
7. Optionally, Microsoft Entra tenant ID, client ID, and client secret.
8. Optionally, an existing Team Hub data directory to migrate.

The installer cannot create organization DNS records or issue company
certificates. It prints the DNS mapping that the infrastructure team must create
and binds the certificate already installed on the server.

## Updates

Run the same batch file again. Select the same IIS site, application pool, and
installation directory. Existing data and configuration are preserved by default
and backed up before application files are updated.

Do not copy development databases over the production data directory. Stop the
Team Hub application pool before restoring a backup.

## Current recovery limitation

Team Hub currently protects credential-encryption keys with Windows DPAPI for the
hosting machine. A copied data/protection-keys directory may not decrypt tokens
on a different server. A move to another machine can therefore require users and
administrators to enter Jira, Confluence, email, or other protected credentials
again. Certificate-protected data-protection keys should be implemented before
requiring portable disaster recovery.
