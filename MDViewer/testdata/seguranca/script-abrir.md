# Script que pede para abrir arquivo

Ao abrir este documento, um script envia `navigate` para `teste.bat` sem clique.
A confirmação deve aparecer e nada deve ser executado sem "Sim".

<script>window.chrome.webview.postMessage(JSON.stringify({type:'navigate',href:'teste.bat'}))</script>