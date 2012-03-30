General principle :
- export as much as possible using clearexport_ccase (in several parts due to memory constraints of clearexport_ccase)
- get all elements (files and directories)
- optionally edit these lists to exclude uninteresting ones
- use GitImporter (which calls cleartool) to create (and save) a representation of the Vob
- import with GitImporter and "git fast-import", cleartool is then used only to get the content of files

FOR /D %D in (*) DO clearexport_ccase -r -o %D.export %D

cleartool find -all -type d -print >directories.lst
cleartool find -all -type f -print >files.lst

GitImporter -S:vobDB.bin -E:files.lst -D:directories.lst -G -C:M:\MyView\MyVob *.export

GitImporter -L:vobDB.bin -C:M:\MyView\MyVob | git fast-import
