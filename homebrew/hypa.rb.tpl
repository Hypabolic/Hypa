class Hypa < Formula
  desc "Local context runtime and terminal multiplexer for coding agents"
  homepage "https://github.com/Hypabolic/Hypa"
  version "PLACEHOLDER_VERSION"
  license "FSL-1.1-ALv2"

  on_macos do
    on_intel do
      url "https://github.com/Hypabolic/Hypa/releases/download/vPLACEHOLDER_VERSION/hypa-osx-x64.tar.gz"
      sha256 "PLACEHOLDER_SHA_OSX_X64"
    end
    on_arm do
      url "https://github.com/Hypabolic/Hypa/releases/download/vPLACEHOLDER_VERSION/hypa-osx-arm64.tar.gz"
      sha256 "PLACEHOLDER_SHA_OSX_ARM64"
    end
  end

  on_linux do
    on_intel do
      url "https://github.com/Hypabolic/Hypa/releases/download/vPLACEHOLDER_VERSION/hypa-linux-x64.tar.gz"
      sha256 "PLACEHOLDER_SHA_LINUX_X64"
    end
    on_arm do
      url "https://github.com/Hypabolic/Hypa/releases/download/vPLACEHOLDER_VERSION/hypa-linux-arm64.tar.gz"
      sha256 "PLACEHOLDER_SHA_LINUX_ARM64"
    end
  end

  # The release archive is a self-contained directory. hypa finds hypa-attach,
  # hypa-runtime, hypa-pty-host, libghostty-vt and the native libraries next to
  # its own executable, so keep them together in libexec and link only hypa.
  # The binaries are prebuilt and signed by the release pipeline; relinking
  # them would invalidate the signatures.
  def install
    libexec.install Dir["*"]
    bin.install_symlink libexec/"hypa"
  end

  test do
    %w[hypa hypa-attach hypa-annotate hypa-runtime hypa-pty-host].each do |program|
      assert_predicate libexec/program, :executable?
    end
    assert_predicate libexec/shared_library("libghostty-vt"), :exist?
    assert_predicate libexec/shared_library("libe_sqlite3"), :exist?

    assert_match version.to_s, shell_output("#{bin}/hypa --version")
  end
end
