# Navegações iniciadas pelo conteúdo

Todas devem ser bloqueadas; a página continua neste documento.

<meta http-equiv="refresh" content="1;url=https://example.com">

<form id="f" action="https://example.com" method="get"><input name="q" value="x"></form>

<script>
setTimeout(function(){ document.getElementById('f').submit(); }, 1500);
setTimeout(function(){ location.href = 'https://example.com/?late'; }, 2500);
setTimeout(function(){ location.reload(); }, 3500);
</script>

Texto de controle: CONTINUA AQUI.