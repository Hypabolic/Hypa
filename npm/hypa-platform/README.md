# @hypabolic/hypa-[platform]

Platform-specific native build of [Hypa](https://www.npmjs.com/package/@hypabolic/hypa) for Linux and macOS on x64 and arm64.

This package holds the whole Hypa release directory: `hypa`, the mux host, the attach client, the PTY helper, and the native libraries. They must stay together.

It is installed automatically as an optional dependency of `@hypabolic/hypa`. Install the main package instead:

```bash
npm install -g @hypabolic/hypa
```

## License

[Functional Source License 1.1, ALv2 Future License](https://github.com/Hypabolic/Hypa/blob/main/license.md)
