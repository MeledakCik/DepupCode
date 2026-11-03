# Privacy Policy — Depup Code

Last updated: 2026-11-03

Depup Code is a desktop application. By default, your code and project files stay on your device.

## Data we collect
- Depup Code does not send telemetry or crash reports automatically. If you choose to submit a bug report, the text and attachments you include are sent to the repository issue service.
- Account identifiers and authentication tokens that you choose to connect or save. Supported Windows builds protect saved credentials with Windows DPAPI on the current Windows user account.
- Project files, environment values, deployment archives, domain information, and provider credentials only when you explicitly use the corresponding cloud sync, AI, Git, extension, deployment, domain, or tunnel feature. The selected provider receives the data needed to perform that action.
- Requests to Depup Cloud, Open VSX, GitHub, AI providers, and PHP/Apache runtime download hosts disclose network metadata such as IP address and request details to those services.

## Data we do not collect
- The core editor does not upload project files merely because a folder is opened.
- We do not require analytics or background telemetry for core editor features.
- Reading or acknowledging this policy does not enable optional cloud sharing. Online features require a separate user action and may transmit the selected content to the named service.

## Local storage and permissions
- Settings, recent workspace paths, project catalog data, and chat history are stored per Windows user. Credentials and selected sensitive local data are protected using Windows DPAPI where supported; this is not protection from malware running as the same user.
- PHP and Apache are downloaded only when requested, verified against pinned SHA-256 digests, and stored in the user's local application data. They are started as local development processes and are not installed as Windows services.
- Depup Code starts terminal, formatter, language-server, debugger, AI, and extension processes when you invoke the associated feature. Review and manage third-party extensions and providers you choose to install or connect.
- The Google sign-in flow opens the system browser and returns through the `depup://` protocol. Logging out removes the Depup Code session on this device; it does not sign you out of Google in the browser or delete cloud account data.

## Third-party services
- Open VSX, GitHub, Vercel, Cloudflare, Railway, Depup Cloud, OpenAI-compatible endpoints, Google Gemini, GitHub Copilot, and other providers you connect are governed by their own terms and privacy policies. AI features may send prompts and selected workspace context to the configured provider to produce a response.
- Runtime downloads (PHP/Apache) are fetched directly from their configured official/vendor sources and verified before extraction.

## Your choices
- Decline the first-run policy acknowledgment to close the application. You can reopen this policy in Settings and clear the saved acknowledgment; the app asks you to review it again on the next launch.
- Use offline mode, avoid optional integrations, remove saved provider credentials in Settings, uninstall the app, or delete local application data from Windows when desired. Uninstalling does not automatically delete separately stored workspace files or all per-user application data.

## Contact
For privacy questions, contact the Depup team through the [project repository](https://github.com/MeledakCik/DepupCode/issues).
