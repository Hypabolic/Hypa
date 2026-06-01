# Hypa

Local context runtime for agentic development — compresses shell output, indexes code and Markdown, and proxies upstream MCP servers.

## Installation

```bash
npm install -g @hypabolic/hypa
```

## Quick start

```bash
# Compress command output
hypa git status
hypa dotnet build
hypa -c "kubectl get pods -A"

# Index your codebase
hypa code index

# Query Markdown structure
hypa md README.md --toc
hypa md docs/guide.md --section "Installation"

# Wire into your agent harness
hypa init --global
```

## Documentation

[hypabolic.dev/products/hypa/docs](https://hypabolic.dev/products/hypa/docs)

## License

[Functional Source License 1.1, ALv2 Future License](https://github.com/Hypabolic/Hypa/blob/main/license.md)
