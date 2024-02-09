{
  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    flake-utils.url = "github:numtide/flake-utils";
  };
  outputs = { nixpkgs, flake-utils, ... }:
    flake-utils.lib.eachDefaultSystem (system:
      let
        pkgs = nixpkgs.legacyPackages.${system};
      in
      {
        devShell = pkgs.mkShell {
          buildInputs = with pkgs; [ fish ];
          shellHook = ''
            export LD_LIBRARY_PATH=${nixpkgs.lib.makeLibraryPath [ pkgs.fontconfig ]}:$LD_LIBRARY_PATH
            exec fish
          '';
        };
      }
    );
}
