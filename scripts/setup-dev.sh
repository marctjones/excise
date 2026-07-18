#!/bin/bash
# Setup complete development environment for excise
# This script requires no user interaction

set -e

# Source common functions
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"

# Initialize logging
init_logging "setup-dev"

log_section "PDF Editor - Development Environment Setup"

log_info "This script will install all required dependencies"
log_info "No user interaction required"
log ""

# Detect OS
detect_os() {
    if [ -f /etc/os-release ]; then
        . /etc/os-release
        OS=$ID
        OS_VERSION=$VERSION_ID
    elif [ "$(uname)" = "Darwin" ]; then
        OS="macos"
        OS_VERSION=$(sw_vers -productVersion)
    else
        OS="unknown"
        OS_VERSION="unknown"
    fi

    log_info "Detected OS: $OS $OS_VERSION"
}

# Install system dependencies (Linux)
install_linux_deps() {
    log_section "Installing System Dependencies"

    # Determine package manager
    if check_command apt-get; then
        PKG_MGR="apt-get"
        PKG_INSTALL="sudo apt-get install -y"
        PKG_UPDATE="sudo apt-get update"
    elif check_command dnf; then
        PKG_MGR="dnf"
        PKG_INSTALL="sudo dnf install -y"
        PKG_UPDATE="sudo dnf check-update || true"
    elif check_command yum; then
        PKG_MGR="yum"
        PKG_INSTALL="sudo yum install -y"
        PKG_UPDATE="sudo yum check-update || true"
    elif check_command pacman; then
        PKG_MGR="pacman"
        PKG_INSTALL="sudo pacman -S --noconfirm"
        PKG_UPDATE="sudo pacman -Sy"
    else
        log_warning "Unknown package manager, skipping system deps"
        return 0
    fi

    log_info "Using package manager: $PKG_MGR"

    # Update package list
    log_info "Updating package list..."
    $PKG_UPDATE >> "$LOG_FILE" 2>&1 || true

    # Install required packages
    local packages=""
    case $PKG_MGR in
        apt-get)
            packages="libgdiplus libfontconfig1 libicu-dev poppler-utils qpdf mupdf-tools"
            ;;
        dnf|yum)
            packages="libgdiplus fontconfig libicu poppler-utils qpdf mupdf"
            ;;
        pacman)
            packages="libgdiplus fontconfig icu poppler qpdf mupdf"
            ;;
    esac

    log_info "Installing: $packages"
    $PKG_INSTALL $packages >> "$LOG_FILE" 2>&1 || {
        log_warning "Some packages may have failed to install"
        log_warning "Check log file for details"
    }

    log_success "System dependencies installed"
}

# Install .NET SDK
install_dotnet() {
    log_section "Installing .NET 8.0 SDK"

    # Check if already installed
    if check_command dotnet; then
        local version=$(dotnet --version)
        if [[ "$version" == 8.* ]]; then
            log_success ".NET 8.x already installed: $version"
            return 0
        fi
    fi

    # Check user's local install
    if [ -f "$HOME/.dotnet/dotnet" ]; then
        local version=$("$HOME/.dotnet/dotnet" --version 2>/dev/null || echo "unknown")
        if [[ "$version" == 8.* ]]; then
            log_success ".NET 8.x already installed in ~/.dotnet: $version"
            export PATH="$HOME/.dotnet:$PATH"
            return 0
        fi
    fi

    log_info "Downloading .NET installer..."

    # Download and run .NET installer
    local installer_url="https://dot.net/v1/dotnet-install.sh"
    local installer_path="/tmp/dotnet-install.sh"

    if check_command curl; then
        curl -sSL "$installer_url" -o "$installer_path"
    elif check_command wget; then
        wget -q "$installer_url" -O "$installer_path"
    else
        log_error "Neither curl nor wget available"
        return 1
    fi

    chmod +x "$installer_path"

    log_info "Installing .NET 8.0 SDK..."
    "$installer_path" --channel 8.0 --install-dir "$HOME/.dotnet" >> "$LOG_FILE" 2>&1

    # Add to PATH
    export PATH="$HOME/.dotnet:$PATH"

    # Verify installation
    if [ -f "$HOME/.dotnet/dotnet" ]; then
        local version=$("$HOME/.dotnet/dotnet" --version)
        log_success ".NET SDK installed: $version"

        # Add to shell profile
        local profile_file=""
        if [ -f "$HOME/.bashrc" ]; then
            profile_file="$HOME/.bashrc"
        elif [ -f "$HOME/.zshrc" ]; then
            profile_file="$HOME/.zshrc"
        elif [ -f "$HOME/.profile" ]; then
            profile_file="$HOME/.profile"
        fi

        if [ -n "$profile_file" ]; then
            if ! grep -q "\.dotnet" "$profile_file" 2>/dev/null; then
                echo "" >> "$profile_file"
                echo "# .NET SDK" >> "$profile_file"
                echo "export PATH=\"\$HOME/.dotnet:\$PATH\"" >> "$profile_file"
                log_info "Added .NET to PATH in $profile_file"
            fi
        fi
    else
        log_error ".NET installation failed"
        return 1
    fi

    rm -f "$installer_path"
}

# Restore NuGet packages
restore_packages() {
    log_section "Restoring NuGet Packages"

    find_dotnet || return 1

    cd "$PROJECT_ROOT"

    # Restore main project
    log_info "Restoring Excise.App packages..."
    $DOTNET_CMD restore Excise.App/Excise.App.csproj >> "$LOG_FILE" 2>&1

    # Restore test project
    log_info "Restoring Excise.App.Tests packages..."
    $DOTNET_CMD restore Excise.App.Tests/Excise.App.Tests.csproj >> "$LOG_FILE" 2>&1

    # Restore demo project if it exists
    if [ -d "Excise.App.Demo" ]; then
        log_info "Restoring Excise.App.Demo packages..."
        $DOTNET_CMD restore Excise.App.Demo/Excise.App.Demo.csproj >> "$LOG_FILE" 2>&1
    fi

    log_success "All packages restored"
}

# Verify installation
verify_installation() {
    log_section "Verifying Installation"

    local all_ok=true

    # Check .NET
    if check_command dotnet || [ -f "$HOME/.dotnet/dotnet" ]; then
        log_success ".NET SDK: OK"
    else
        log_error ".NET SDK: NOT FOUND"
        all_ok=false
    fi

    # Check PDF tools
    if check_command pdftotext; then
        log_success "pdftotext: OK ($(pdftotext -v 2>&1 | head -1 || echo 'installed'))"
    else
        log_warning "pdftotext: NOT FOUND (optional, for validation)"
    fi

    if check_command qpdf; then
        log_success "qpdf: OK ($(qpdf --version | head -1))"
    else
        log_warning "qpdf: NOT FOUND (optional, for validation)"
    fi

    if check_command strings; then
        log_success "strings: OK"
    else
        log_warning "strings: NOT FOUND"
    fi

    if check_command mutool; then
        log_success "mutool: OK"
    else
        log_warning "mutool: NOT FOUND (optional, for validation)"
    fi

    if [ "$all_ok" = true ]; then
        log_success "All required dependencies installed"
        return 0
    else
        log_error "Some required dependencies are missing"
        return 1
    fi
}

# Main execution
main() {
    detect_os

    # Install dependencies based on OS
    case $OS in
        ubuntu|debian|linuxmint|pop)
            install_linux_deps
            ;;
        fedora|centos|rhel|rocky|alma)
            install_linux_deps
            ;;
        arch|manjaro)
            install_linux_deps
            ;;
        macos)
            log_info "macOS detected"
            log_info "Please install Homebrew dependencies manually:"
            log_info "  brew install mono-libgdiplus poppler qpdf mupdf-tools"
            ;;
        *)
            log_warning "Unknown OS: $OS"
            log_warning "Skipping system dependency installation"
            ;;
    esac

    install_dotnet || exit 1
    restore_packages || exit 1
    verify_installation

    print_summary "setup-dev" "success"

    log ""
    log "Next steps:"
    log "  1. Build the project:    ./scripts/build.sh"
    log "  2. Run tests:            ./scripts/test.sh"
    log "  3. Run CLI demo:         ./scripts/demo-excise-cli.sh"
    log "  4. Launch GUI:           ./scripts/gui.sh"
    log ""
}

main "$@"
