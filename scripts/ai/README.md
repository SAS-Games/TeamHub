# Team Hub local AI tools

These scripts do not download or install third-party software. Use only llama.cpp
binaries and GGUF models approved by your organization.

## Start llama.cpp

~~~powershell
./Start-TeamHubLlamaCpp.ps1 -ServerPath D:/TeamHubAI/llama-server.exe -ModelPath D:/TeamHubAI/Models/Qwen3-8B-Q4_K_M.gguf -ModelAlias qwen3:8b -GpuLayers 99
~~~

Omit -GpuLayers for CPU-only execution. Supply -ExpectedServerSha256 and
-ExpectedModelSha256 when your organization publishes approved checksums.
The server binds only to 127.0.0.1.

For Flow Designer structured generation, use an instruction-tuned model with at
least 4 billion parameters; Qwen3 8B Q4_K_M is the recommended baseline. Very
small models such as Qwen3 0.6B are useful for runtime connectivity checks but
do not reliably preserve node references, hierarchy constraints, or evidence
links required by Flow Designer.

## Validate the runtime

In another PowerShell window:

~~~powershell
./Test-TeamHubLocalAi.ps1 -Endpoint http://127.0.0.1:8080 -Model qwen3:8b
~~~

## Select the Team Hub provider

Team Hub keeps separate profiles for Ollama and llama.cpp. Change only the active
provider:

~~~text
AI__Provider=LlamaCpp
~~~

Switch back with:

~~~text
AI__Provider=Ollama
~~~

For local development, the same value can be changed in
src/TeamHub.Web/appsettings.json. Restart Team Hub after switching. For IIS,
set the environment variable on the Team Hub application pool and recycle the
pool.

The llama.cpp profile defaults to http://127.0.0.1:8080 and model alias
qwen3:8b. Override either value with:

~~~text
AI__Providers__LlamaCpp__Endpoint=http://127.0.0.1:8080
AI__Providers__LlamaCpp__Model=qwen3:8b
~~~
