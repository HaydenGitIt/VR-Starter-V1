"""Synthetic training data for the director model: conversations, grades and exports.

Shared by notebooks/synthetic_conversations.ipynb and three commands, run from the
server folder:

    .venv/Scripts/python -m datagen.generate   play conversations, one process per teacher
    .venv/Scripts/python -m datagen.grade      grade every teacher line, several graders at once
    .venv/Scripts/python -m datagen.export     write Laya training files from the grades
    .venv/Scripts/python -m datagen.train_director   fine-tune Laya as the server's director

See datagen/README.md.
"""
