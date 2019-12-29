# DropboxEncryptedUploader
Synchronizes local folder to Dropbox encrypting each file to a separate zip archive. Works in memory without storing to disk, which means that each file is devided by 140mb blocks and each block is encrypted on the fly, immediately sent to Dropbox servers and then thrown away so the whole encrypted archive (be it even 300 GB) will not take much space on your machine.
## Usage
DropboxEncryptedUploader.exe dropbox-app-token path-to-folder dropbox-folder-name encryption-password
### Example
DropboxEncryptedUploader.exe "asdlakdfkfrefggfdgdfg-rgedfgd-adfsfdf3e" "d:\Backups" "/Backups" "password"

## How to get Dropbox token
http://99rabbits.com/get-dropbox-access-token/
