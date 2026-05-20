class Hypa < Formula
  desc "Local context runtime for agentic development"
  homepage "https://github.com/Hypabolic/Hypa"
  version "PLACEHOLDER_VERSION"
  license "MIT"

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

  def install
    bin.install Dir["hypa-*/hypa"].first => "hypa"
  end

  test do
    assert_match version.to_s, shell_output("#{bin}/hypa --version")
  end
end
