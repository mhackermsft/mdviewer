# Teste de segurança (001)

- [instalar](teste.bat): arquivo local não-Markdown, deve pedir confirmação
- [outro](outro.md): Markdown, abre no viewer sem confirmação
- [site](https://example.com): URL, abre no navegador sem confirmação
- [seção](#fim): âncora, rola dentro da página

Script abaixo tenta sair da página (deve ser bloqueado):

<script>location.href='https://example.com'</script>

## Fim {#fim}

Se você está lendo isto, a página não foi trocada.