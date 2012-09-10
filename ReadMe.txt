There are samples of actual use in the "scripts" directory

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


Thirdparties :
There is support for thirdparties to be handled as git submodules, using a specific configuration file.
It assumes that there is a special file that stores the clearcase config-spec, with label rules
for some directories. Then for each new version of this file, if a match is found for a directory
and a label, the corresponding commit of the submodule will be referenced.
